namespace CodexSyncBar.Windows.Core;

// This is deliberately a presentation-only contract. Never put credentials, auth JSON,
// remote paths, or SSH connection configuration in a widget/pipe response.
public sealed record DashboardSnapshot
{
    public string ConfigurationRevision { get; init; } = string.Empty;
    public IReadOnlyList<DashboardAccount> Accounts { get; init; } = [];
    public IReadOnlyList<DashboardDevice> Devices { get; init; } = [];
    public int? ActiveProfileId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsBusy { get; init; }
    public SwitchOperation? Operation { get; init; }
    public string? Error { get; init; }
}

public sealed record DashboardAccount(
    int ProfileId, string DisplayName, string MaskedEmail, bool NeedsLogin,
    DashboardUsage? Usage = null);

public sealed record DashboardUsage(
    UsageWindow? Session, UsageWindow? Weekly, int? ResetCredits,
    IReadOnlyList<DateTimeOffset> ResetCreditExpirations, DateTimeOffset UpdatedAt)
{
    public string? Error { get; init; }
}

public sealed record DashboardDevice(
    string Id, string DisplayName, string Kind, int? ProfileId, bool IsReachable,
    string? Detail = null);

public sealed record SwitchOperation
{
    public string Id { get; init; } = string.Empty;
    public int ProfileId { get; init; }
    public string State { get; init; } = "queued";
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; init; }
    public IReadOnlyList<SwitchTargetResult> Targets { get; init; } = [];
    public bool IsComplete => State is "completed" or "failed" or "recoveryRequired";
}

public sealed record SwitchTargetResult(
    string Id, string DisplayName, string State, string? Detail = null);

public sealed class DashboardPipeHandlers
{
    public required Func<CancellationToken, Task<DashboardSnapshot>> GetSnapshotAsync { get; init; }
    public required Func<CancellationToken, Task<DashboardSnapshot>> RefreshUsageAsync { get; init; }
    public required Func<int, string, CancellationToken, Task<SwitchOperation>> SwitchAccountAsync { get; init; }
    public required Func<string, CancellationToken, Task<SwitchOperation?>> GetOperationStatusAsync { get; init; }
    public required Func<CancellationToken, Task> OpenSettingsAsync { get; init; }
}
