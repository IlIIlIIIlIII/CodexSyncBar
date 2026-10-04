using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodexSyncBar.Windows.Core;
using CodexSyncBar.Ubuntu.Backend;

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ubuntu 전용 backend입니다.");
PeerIdentity.RestrictCreatedFiles();
if (args.Contains("--askpass"))
{
    var credential = Environment.GetEnvironmentVariable("CODEX_SYNCBAR_CREDENTIAL_ID");
    var kind = Environment.GetEnvironmentVariable("CODEX_SYNCBAR_SECRET_KIND");
    if (!Guid.TryParse(credential, out var id) || kind is not ("password" or "passphrase")) return 64;
    try
    {
        var secret = new WindowsSecretStore(new WindowsPaths()).Read(id.ToString("D") + "." + kind);
        if (string.IsNullOrEmpty(secret)) return 1;
        Console.Out.Write(secret);
        return 0;
    }
    catch { return 1; }
}
string? Option(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var demo = args.Contains("--demo");
var socketPath = Option("--socket");
if (demo && socketPath is null) { Console.Error.WriteLine("--demo에는 격리된 --socket 경로가 필요합니다."); return 64; }
socketPath ??= Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
    ?? throw new InvalidOperationException("사용자 데스크톱 세션에서 시작해 주세요."), "codex-syncbar", "control.sock");
socketPath = Path.GetFullPath(socketPath);
var socketDirectory = Path.GetDirectoryName(socketPath)!;
WindowsPathSafety.EnsurePrivateDirectory(socketDirectory, "SyncBar 연결 디렉터리");
WindowsPathSafety.EnsurePrivateFile(socketPath + ".lock", "SyncBar 서비스 잠금", 4096);
using var lockFile = new FileStream(socketPath + ".lock", new FileStreamOptions
{
    Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
});
if (LinuxFileMetadata.TryRead(socketPath, out var existingSocket))
{
    if ((existingSocket.Mode & 0xf000) != 0xc000 || existingSocket.Uid != LinuxFileMetadata.CurrentUid)
        throw new IOException("기존 연결 경로가 현재 사용자의 소켓이 아닙니다.");
    File.Delete(socketPath);
}
using var lifetime = new CancellationTokenSource();
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; lifetime.Cancel(); });
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
using IManagementService service = demo ? new DemoService() : new ManagementService(new WindowsPaths());
using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
listener.Bind(new UnixDomainSocketEndPoint(socketPath));
File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
listener.Listen(16);
var connections = new List<Task>();
using var slots = new SemaphoreSlim(16);
Console.Out.WriteLine(JsonSerializer.Serialize(new { ready = true, socket = socketPath, demo }, Wire.Json));
try
{
    while (!lifetime.IsCancellationRequested)
    {
        await slots.WaitAsync(lifetime.Token);
        Socket socket;
        try { socket = await listener.AcceptAsync(lifetime.Token); }
        catch { slots.Release(); throw; }
        connections.RemoveAll(t => t.IsCompleted);
        connections.Add(ServeAsync(socket));
    }
}
catch (OperationCanceledException) { }
finally
{
    listener.Close();
    lifetime.Cancel();
    try { await Task.WhenAll(connections); } catch { }
    await service.StopAsync();
    if (LinuxFileMetadata.TryRead(socketPath, out var finalSocket) && (finalSocket.Mode & 0xf000) == 0xc000
        && finalSocket.Uid == LinuxFileMetadata.CurrentUid) File.Delete(socketPath);
}
return 0;

async Task ServeAsync(Socket socket)
{
    using (socket)
    {
        try
        {
            if (!PeerIdentity.SameUser(socket)) return;
            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            while (!lifetime.IsCancellationRequested)
            {
                var line = await ReadLimitedLineAsync(reader, lifetime.Token);
                if (line is null) break;
                string? requestId = null;
                RpcResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<RpcRequest>(line, Wire.Json) ?? throw new JsonException();
                    requestId = request.Id;
                    if (string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 128 || string.IsNullOrWhiteSpace(request.Method))
                        throw new RpcException("invalid_request", "요청 식별자가 올바르지 않습니다.");
                    var result = await service.DispatchAsync(request.Method, request.Params, lifetime.Token);
                    response = new(request.Id, true, result);
                }
                catch (RpcException e) { response = new(requestId, false, Error: new(e.Code, e.Message, e.DataValue)); }
                catch (JsonException) { response = new(requestId, false, Error: new("invalid_request", "요청 형식이 올바르지 않습니다.")); }
                catch (OperationCanceledException) { response = new(requestId, false, Error: new("cancelled", "작업이 취소되었습니다.")); }
                catch { response = new(requestId, false, Error: new("operation_failed", "작업을 완료하지 못했습니다. 연결 상태와 계정 설정을 확인해 주세요.")); }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Wire.Json));
                if (service.ShutdownRequested) { lifetime.Cancel(); break; }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or DecoderFallbackException) { }
        finally { slots.Release(); }
    }
}

static async Task<string?> ReadLimitedLineAsync(StreamReader reader, CancellationToken token)
{
    var text = new StringBuilder();
    var buffer = new char[1];
    while (await reader.ReadAsync(buffer.AsMemory(), token) != 0)
    {
        if (buffer[0] == '\n') return text.ToString();
        if (text.Length >= 65536) throw new IOException("Request too large");
        text.Append(buffer[0]);
    }
    return text.Length == 0 ? null : throw new IOException("Truncated request");
}

internal static class PeerIdentity
{
    [StructLayout(LayoutKind.Sequential)] private struct Ucred { public int Pid; public uint Uid; public uint Gid; }
    [DllImport("libc", SetLastError = true)] private static extern int getsockopt(int fd, int level, int option, out Ucred value, ref uint length);
    [DllImport("libc")] private static extern uint geteuid();
    [DllImport("libc")] private static extern uint umask(uint mask);
    public static void RestrictCreatedFiles() => umask(0x3f);
    public static bool SameUser(Socket socket)
    {
        uint length = (uint)Marshal.SizeOf<Ucred>();
        return getsockopt(socket.Handle.ToInt32(), 1, 17, out var value, ref length) == 0 && value.Uid == geteuid();
    }
}
