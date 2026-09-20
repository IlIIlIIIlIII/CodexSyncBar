using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace CodexSyncBar.Windows.Core;

/// <summary>Presentation-only local RPC. The OS enforces the current user's identity.</summary>
public sealed class DashboardPipeServer : IAsyncDisposable
{
    private readonly DashboardPipeHandlers handlers;
    private readonly string pipeName;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim connectionSlots = new(16, 16);
    private readonly ConcurrentDictionary<long, Task> connections = new();
    private readonly ConcurrentDictionary<string, (DashboardPipeRequest Request, Lazy<Task<DashboardPipeResponse>> Response)> actions = new();
    private Task? acceptTask;
    private long connectionId;

    public DashboardPipeServer(DashboardPipeHandlers handlers, string? pipeName = null)
    {
        this.handlers = handlers;
        this.pipeName = pipeName ?? DefaultPipeName;
    }

    public static string DefaultPipeName { get; } = CreateDefaultPipeName();

    private static string CreateDefaultPipeName()
    {
        string identity;
        if (OperatingSystem.IsWindows())
        {
            using var user = WindowsIdentity.GetCurrent();
            identity = user.User?.Value ?? Environment.UserName;
        }
        else identity = Environment.UserName;
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        return $"CodexSyncBar.Dashboard.v1.{suffix}";
    }

    public void Start()
    {
        if (acceptTask is not null) throw new InvalidOperationException("위젯 연결 서버가 이미 시작되었습니다.");
        acceptTask = AcceptAsync(lifetime.Token);
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream CreatePipe() => new(pipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var listener = CreatePipe();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await connectionSlots.WaitAsync(cancellationToken);
                await listener.WaitForConnectionAsync(cancellationToken);
                var connected = listener;
                // Keep the pipe name continuously registered, including when malformed
                // requests finish synchronously and dispose their connected instance.
                listener = CreatePipe();
                var id = Interlocked.Increment(ref connectionId);
                var task = HandleAsync(connected, cancellationToken);
                connections[id] = task;
                _ = task.ContinueWith(_ =>
                {
                    connections.TryRemove(id, out var removed);
                    connectionSlots.Release();
                }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { await listener.DisposeAsync(); }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var request = await DashboardPipeProtocol.ReadAsync<DashboardPipeRequest>(
                    pipe, DashboardPipeProtocol.MaximumRequestBytes, readDeadline.Token);
                DashboardPipeResponse response;
                if (!DashboardPipeProtocol.IsValid(request))
                    response = new(false, Error: "지원하지 않는 위젯 요청입니다.", ErrorCode: "invalid_request");
                else if (request.Method == "SwitchAccount")
                {
                    // Keep at most 512 mutation receipts for this process. An old request is
                    // rejected rather than re-executed after eviction, including lost replies.
                    if (actions.Count >= 512 && !actions.ContainsKey(request.RequestId))
                        response = new(false, Error: "전환 요청이 너무 많습니다. 앱을 다시 시작해 주세요.", ErrorCode: "request_limit");
                    else
                    {
                        var saved = actions.GetOrAdd(request.RequestId, _ =>
                            (request, new Lazy<Task<DashboardPipeResponse>>(() => DispatchAsync(request, cancellationToken))));
                        response = saved.Request == request
                            ? await saved.Response.Value
                            : new(false, Error: "중복 요청의 내용이 달라 처리하지 않았습니다.", ErrorCode: "request_conflict");
                    }
                }
                else response = await DispatchAsync(request, cancellationToken);

                using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                writeDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                await DashboardPipeProtocol.WriteAsync(pipe, response, writeDeadline.Token);
            }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException)
            {
                // Malformed, truncated, oversized, or disconnected clients cannot stop the accept loop.
            }
        }
    }

    private async Task<DashboardPipeResponse> DispatchAsync(DashboardPipeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Method)
            {
                case "GetSnapshot": return new(true, Snapshot: await handlers.GetSnapshotAsync(cancellationToken));
                case "RefreshUsage": return new(true, Snapshot: await handlers.RefreshUsageAsync(cancellationToken));
                case "GetOperationStatus": return new(true, Operation: await handlers.GetOperationStatusAsync(request.OperationId!, cancellationToken));
                case "OpenSettings":
                    await handlers.OpenSettingsAsync(cancellationToken);
                    return new(true);
                case "SwitchAccount":
                    var snapshot = await handlers.GetSnapshotAsync(cancellationToken);
                    if (snapshot.ConfigurationRevision != request.ConfigurationRevision)
                        return new(false, Snapshot: snapshot, Error: "계정 또는 장치 구성이 변경되었습니다. 새로고침 후 다시 적용해 주세요.", ErrorCode: "stale_configuration");
                    if (!snapshot.Accounts.Any(a => a.ProfileId == request.ProfileId && !a.NeedsLogin))
                        return new(false, Snapshot: snapshot, Error: "사용할 수 없는 계정입니다. 앱에서 로그인을 확인해 주세요.", ErrorCode: "account_unavailable");
                    if (snapshot.IsBusy)
                        return new(false, Snapshot: snapshot, Operation: snapshot.Operation, Error: "이미 계정을 전환하고 있습니다.", ErrorCode: "busy");
                    return new(true, Operation: await handlers.SwitchAccountAsync(request.ProfileId!.Value, request.ConfigurationRevision!, cancellationToken));
                default: return new(false, Error: "지원하지 않는 위젯 요청입니다.", ErrorCode: "invalid_request");
            }
        }
        catch (AuthenticationRequiredException)
        {
            return new(false, Error: "앱에서 계정 로그인이 필요합니다.", ErrorCode: "login_required");
        }
        catch (Exception)
        {
            // Never return arbitrary exception messages: helper/HTTP errors can contain auth data.
            return new(false, Error: "작업을 완료하지 못했습니다. 앱에서 장치 상태와 복구 결과를 확인해 주세요.", ErrorCode: "operation_failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (acceptTask is not null)
            try { await acceptTask; } catch (OperationCanceledException) { }
        await Task.WhenAll(connections.Values);
        lifetime.Dispose();
    }
}
