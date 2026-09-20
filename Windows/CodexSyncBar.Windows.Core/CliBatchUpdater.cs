namespace CodexSyncBar.Windows.Core;

public sealed record CliUpdateTarget(string Id, string DisplayName, ICliManagementService Service,
    Func<CancellationToken, Task<string?>>? Preflight = null);

public enum CliDeviceUpdateState { Queued, Checking, Updating, Completed, ReconnectPending, Failed, Skipped }

public sealed record CliDeviceUpdate(string DeviceId, CliDeviceUpdateState State, string Message,
    string? Before = null, string? After = null);

public sealed class CliBatchUpdater
{
    public Task<CliDeviceUpdate[]> RunAsync(IReadOnlyList<CliUpdateTarget> targets,
        IProgress<CliDeviceUpdate>? progress = null, CancellationToken cancellationToken = default)
    {
        if (targets.Select(target => target.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
            throw new ArgumentException("업데이트 대상에 중복된 기기가 있습니다.", nameof(targets));
        // Process startup/stdin can block briefly; keep it off the UI thread.
        // One failed or slow device never serializes or cancels its siblings.
        return Task.WhenAll(targets.Select(target => Task.Run(async () =>
        {
            string? before = null;
            CliDeviceUpdate Report(CliDeviceUpdateState state, string message, string? after = null)
            {
                var update = new CliDeviceUpdate(target.Id, state, message, before, after);
                progress?.Report(update);
                return update;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(12));
            try
            {
                Report(CliDeviceUpdateState.Checking, "연결·설치 버전 확인 중");
                if (target.Preflight is not null && await target.Preflight(deadline.Token) is { } reason)
                    return Report(CliDeviceUpdateState.Skipped, reason);
                var installation = await target.Service.InspectAsync(deadline.Token);
                before = installation.Version;
                if (!installation.CanUpdate)
                    return Report(CliDeviceUpdateState.Skipped, installation.Notice);
                Report(CliDeviceUpdateState.Updating, "업데이트·버전 검증·재연결 진행 중");
                var result = await target.Service.UpdateAsync(deadline.Token);
                before = result.Before;
                return Report(result.Restart is "reconnected" or "not-running" ? CliDeviceUpdateState.Completed : CliDeviceUpdateState.ReconnectPending,
                    result.DisplayText, result.After);
            }
            catch (OperationCanceledException)
            {
                return Report(CliDeviceUpdateState.Failed, "작업이 취소되거나 제한 시간을 초과했습니다. 기기의 설치 상태를 확인하고 다시 시도해 주세요.");
            }
            catch (Exception error)
            {
                return Report(CliDeviceUpdateState.Failed, error.Message);
            }
        }, CancellationToken.None)));
    }
}
