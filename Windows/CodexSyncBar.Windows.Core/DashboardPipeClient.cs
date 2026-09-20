using System.IO.Pipes;

namespace CodexSyncBar.Windows.Core;

public sealed class DashboardPipeClient(string? pipeName = null, Func<CancellationToken, Task>? launchApp = null)
{
    private readonly string name = pipeName ?? DashboardPipeServer.DefaultPipeName;

    public async Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default) =>
        (await SendAsync(new("GetSnapshot", NewId()), ct)).Snapshot
        ?? throw new InvalidDataException("앱 상태를 받지 못했습니다.");

    public async Task<DashboardSnapshot> RefreshUsageAsync(CancellationToken ct = default) =>
        (await SendAsync(new("RefreshUsage", NewId()), ct)).Snapshot
        ?? throw new InvalidDataException("사용량을 받지 못했습니다.");

    public async Task<SwitchOperation> SwitchAccountAsync(int profileId, string revision, CancellationToken ct = default) =>
        (await SendAsync(new("SwitchAccount", NewId(), profileId, revision), ct)).Operation
        ?? throw new InvalidDataException("계정 전환 결과를 받지 못했습니다.");

    public async Task<SwitchOperation?> GetOperationStatusAsync(string id, CancellationToken ct = default) =>
        (await SendAsync(new("GetOperationStatus", NewId(), OperationId: id), ct)).Operation;

    public async Task OpenSettingsAsync(CancellationToken ct = default) =>
        _ = await SendAsync(new("OpenSettings", NewId()), ct);

    public async Task<DashboardPipeResponse> SendAsync(DashboardPipeRequest request, CancellationToken ct = default)
    {
        if (!DashboardPipeProtocol.IsValid(request)) throw new ArgumentException("잘못된 위젯 요청입니다.", nameof(request));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(750, deadline.Token); }
        catch (TimeoutException) when (launchApp is not null)
        {
            await launchApp(deadline.Token);
            await pipe.ConnectAsync(15000, deadline.Token);
        }
        await DashboardPipeProtocol.WriteAsync(pipe, request, deadline.Token);
        var response = await DashboardPipeProtocol.ReadAsync<DashboardPipeResponse>(
            pipe, DashboardPipeProtocol.MaximumFrameBytes, deadline.Token);
        if (!response.Success)
            throw new DashboardPipeException(response.ErrorCode ?? "operation_failed", response.Error ?? "작업에 실패했습니다.");
        return response;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
