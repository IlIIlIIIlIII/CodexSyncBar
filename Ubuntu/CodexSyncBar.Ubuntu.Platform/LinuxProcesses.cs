using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexSyncBar.Windows.Core;

/// <summary>Same-user process evidence and pidfd-pinned signalling; never signal by a reused PID.</summary>
internal sealed class LinuxProcess : IDisposable
{
    public int Id { get; }
    public int ParentId { get; }
    public ulong StartTime { get; }
    public string Executable { get; }
    public string[] Arguments { get; }
    public IReadOnlyDictionary<string, string> Environment { get; }
    public bool EnvironmentReadable { get; }
    private readonly int _descriptor;
    private LinuxProcess(int id, int parentId, ulong startTime, int descriptor, string executable, string[] args, Dictionary<string,string> environment, bool readable)
    { Id = id; ParentId = parentId; StartTime = startTime; _descriptor = descriptor; Executable = executable; Arguments = args; Environment = environment; EnvironmentReadable = readable; }
    [DllImport("libc", SetLastError = true)] private static extern int pidfd_open(int pid, uint flags);
    [DllImport("libc", SetLastError = true)] private static extern int pidfd_send_signal(int fd, int signal, IntPtr info, uint flags);
    [DllImport("libc")] private static extern int close(int fd);
    [StructLayout(LayoutKind.Sequential)] private struct PollFd { public int Fd; public short Events; public short Revents; }
    [DllImport("libc", SetLastError = true)] private static extern int poll(ref PollFd fds, uint count, int timeout);

    public static IEnumerable<LinuxProcess> Enumerate()
    {
        foreach (var path in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(path), out var id) || id == System.Environment.ProcessId) continue;
            LinuxProcess? value = null;
            try { value = Capture(id); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            if (value is not null) yield return value;
        }
    }
    internal static LinuxProcess? Capture(int id)
    {
        var path = "/proc/" + id;
        if (!LinuxFileMetadata.TryRead(path, out var metadata) || metadata.Uid != LinuxFileMetadata.CurrentUid) return null;
        var descriptor = pidfd_open(id, 0);
        if (descriptor < 0) return null;
        try
        {
            var (parentId, startTime) = ReadIdentity(path);
            var executable = new FileInfo(path + "/exe").ResolveLinkTarget(true)?.FullName;
            if (executable is null) return null;
            var args = Encoding.UTF8.GetString(File.ReadAllBytes(path + "/cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (args.Length == 0) return null;
            var name = Path.GetFileName(args[0]);
            var relevant = name is "codex" or "codex.js" or "codex-app-server" or "Codex" or "ChatGPT" or "codex-desktop" or "chrome" or "chromium" or "chromium-browser"
                || name is "node" or "nodejs" && args.Contains("app-server");
            // Electron can overwrite argv/environ with a single process title. The
            // kernel executable remains authoritative even when argv[0] is flattened.
            if (!relevant && !CodexDesktopLifecycle.IsDesktopExecutable(executable)) return null;
            var environment = new Dictionary<string,string>(StringComparer.Ordinal);
            var readable = true;
            try
            {
                foreach (var field in Encoding.UTF8.GetString(File.ReadAllBytes(path + "/environ")).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    var separator = field.IndexOf('=');
                    if (separator > 0) environment[field[..separator]] = field[(separator+1)..];
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { readable = false; }
            if (ReadIdentity(path) != (parentId, startTime)) return null;
            var value = new LinuxProcess(id, parentId, startTime, descriptor, executable, args, environment, readable);
            if (value.HasExited) return null;
            descriptor = -1;
            return value;
        }
        finally { if (descriptor >= 0) close(descriptor); }
    }
    private static (int ParentId, ulong StartTime) ReadIdentity(string path)
    {
        // comm may contain spaces and parentheses; the final ')' terminates it.
        var stat = File.ReadAllText(path + "/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture),
            ulong.Parse(fields[19], System.Globalization.CultureInfo.InvariantCulture));
    }
    internal static string? GetCodexHome(IReadOnlyDictionary<string, string> environment, bool readable = true)
    {
        if (!readable) return null;
        var home = environment.GetValueOrDefault("CODEX_HOME");
        if (string.IsNullOrEmpty(home))
        {
            var userHome = environment.GetValueOrDefault("HOME");
            if (string.IsNullOrEmpty(userHome)) return null;
            home = Path.Combine(userHome, ".codex");
        }
        return Path.IsPathFullyQualified(home) ? Path.GetFullPath(home) : null;
    }
    public string? CodexHome => GetCodexHome(Environment, EnvironmentReadable);
    public bool UsesCodexHome(WindowsPaths paths) => CodexHome == paths.CodexHome;
    public bool IsAppServer
    {
        get
        {
            if (!Arguments.Contains("app-server")) return false;
            var command = Path.GetFileName(Arguments[0]);
            var codex = command is "codex" or "codex.js" or "codex-app-server"
                || command is "node" or "nodejs" && Arguments.Skip(1).Any(a => Path.GetFileName(a) is "codex.js" or "codex");
            if (!codex) return false;
            for (var i = 0; i < Arguments.Length; i++)
                if (Arguments[i].StartsWith("--listen=", StringComparison.Ordinal) && !Arguments[i].StartsWith("--listen=unix://", StringComparison.Ordinal)
                    || Arguments[i] == "--listen" && i + 1 < Arguments.Length && !Arguments[i + 1].StartsWith("unix://", StringComparison.Ordinal)) return false;
            return true;
        }
    }
    public bool UsesBrowserProfile(string directory)
    {
        var values = new List<string>();
        for (var i=0; i<Arguments.Length; i++)
        {
            if (Arguments[i].StartsWith("--user-data-dir=", StringComparison.Ordinal)) values.Add(Arguments[i][16..]);
            else if (Arguments[i] == "--user-data-dir" && i+1<Arguments.Length) values.Add(Arguments[++i]);
        }
        return values.Count == 1 && Path.IsPathFullyQualified(values[0]) && Path.GetFullPath(values[0]) == Path.GetFullPath(directory);
    }
    public bool HasExited
    {
        get { var value = new PollFd { Fd = _descriptor, Events = 1 }; return poll(ref value, 1, 0) > 0; }
    }
    public async Task StopAsync(CancellationToken token)
    {
        if (HasExited) return;
        Signal(15);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!HasExited && DateTime.UtcNow < deadline) await Task.Delay(50, token);
        if (!HasExited)
        {
            Signal(9);
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (!HasExited && DateTime.UtcNow < deadline) await Task.Delay(50, token);
        }
        if (!HasExited) throw new CodexSyncBarException("관리 대상 프로세스의 종료를 확인하지 못했습니다.");
    }
    private void Signal(int signal)
    {
        if (pidfd_send_signal(_descriptor, signal, IntPtr.Zero, 0) != 0 && Marshal.GetLastPInvokeError() != 3)
            throw new CodexSyncBarException("관리 대상 프로세스를 종료하지 못했습니다.");
    }
    public void Dispose() => close(_descriptor);
}
