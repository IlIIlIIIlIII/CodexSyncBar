namespace ClaudeSyncBar.Windows.Core;

public sealed record SshBootstrapResult(
    string DeviceId,
    int ActiveProfileId,
    int InstalledProfileCount);
