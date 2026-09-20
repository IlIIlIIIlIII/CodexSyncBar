using CodexSyncBar.Windows.Core;

namespace CodexSyncBar_Windows;

/// <summary>Memory-only QA fixture: no filesystem paths, auth stores, login, or device adapters.</summary>
public sealed class DemoDashboardRuntime
{
    private readonly object _gate = new();
    private DashboardSnapshot _snapshot = CreateSnapshot();
    public event EventHandler? Changed;

    public DashboardPipeHandlers CreateHandlers(Func<CancellationToken, Task> openSettings) => new()
    {
        GetSnapshotAsync = GetSnapshotAsync,
        RefreshUsageAsync = RefreshUsageAsync,
        SwitchAccountAsync = SwitchAccountAsync,
        GetOperationStatusAsync = GetOperationStatusAsync,
        OpenSettingsAsync = openSettings,
    };

    public Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_snapshot);
    }

    public Task<DashboardSnapshot> RefreshUsageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _snapshot = _snapshot with { UpdatedAt = DateTimeOffset.UtcNow };
        Changed?.Invoke(this, EventArgs.Empty);
        return GetSnapshotAsync(cancellationToken);
    }

    public Task<SwitchOperation?> GetOperationStatusAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_snapshot.Operation?.Id == id ? _snapshot.Operation : null);
    }

    public Task<SwitchOperation> SwitchAccountAsync(int profileId, string revision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SwitchOperation operation;
        lock (_gate)
        {
            if (_snapshot.ConfigurationRevision != revision || !_snapshot.Accounts.Any(a => a.ProfileId == profileId))
                throw new InvalidOperationException("계정 구성이 바뀌었습니다. 새로고침해 주세요.");
            if (_snapshot.IsBusy) throw new InvalidOperationException("계정 전환이 이미 진행 중입니다.");
            operation = new SwitchOperation
            {
                Id = Guid.NewGuid().ToString("N"), ProfileId = profileId,
                State = "applying", Message = "데모 장치의 계정을 전환하고 있습니다.",
                Targets = _snapshot.Devices.Select(d => new SwitchTargetResult(d.Id, d.DisplayName, "applying")).ToArray(),
            };
            _snapshot = _snapshot with { IsBusy = true, Operation = operation };
        }
        Changed?.Invoke(this, EventArgs.Empty);
        _ = CompleteAsync(operation);
        return Task.FromResult(operation);
    }

    private async Task CompleteAsync(SwitchOperation operation)
    {
        await Task.Delay(500);
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                ActiveProfileId = operation.ProfileId, IsBusy = false,
                Devices = _snapshot.Devices.Select(d => d with { ProfileId = operation.ProfileId }).ToArray(),
                Operation = operation with
                {
                    State = "completed", Message = "데모 계정 전환을 완료했습니다. 실제 계정과 장치는 변경되지 않았습니다.",
                    FinishedAt = DateTimeOffset.UtcNow,
                    Targets = operation.Targets.Select(t => t with { State = "verified" }).ToArray(),
                },
            };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static DashboardSnapshot CreateSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        DashboardUsage Usage(double session, double weekly) => new(
            new UsageWindow(session, now.AddHours(3), 18_000),
            new UsageWindow(weekly, now.AddDays(4), 604_800), 3, [now.AddDays(7)], now);
        return new DashboardSnapshot
        {
            ConfigurationRevision = "demo-v1", ActiveProfileId = 1, UpdatedAt = now,
            Accounts = [new(1, "개인 계정", "p***@example.com", false, Usage(28, 42)),
                        new(2, "작업용 계정", "w***@example.com", false, Usage(65, 18))],
            Devices = [new("local", "이 Windows PC", "windows", 1, true),
                       new("wsl:Ubuntu-26.04", "Ubuntu-26.04", "wsl", 1, true),
                       new("ssh:demo", "개발 서버", "ssh", 1, true)],
        };
    }
}
