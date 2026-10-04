namespace CodexSyncBar.Windows.Core;

// The shared controller's provider hooks stay source compatible with Windows.
// Ubuntu never discovers, enables, starts or mutates a WSL distribution.
public sealed class WslDeviceConfiguration
{
    public string Distribution { get; set; } = "";
    public string Username { get; set; } = "";
    public string HomeDirectory { get; set; } = "";
    public bool Enabled { get; set; }
    public bool IsInstalled { get; set; }
    public string Id => "wsl:" + Distribution;
}
public sealed record WslDistribution(string Name, bool IsRunning);
public sealed class WslConfigurationStore(WindowsPaths paths)
{
    public IReadOnlyList<WslDeviceConfiguration> Load() => [];
    public void Save(IEnumerable<WslDeviceConfiguration> devices) => throw new PlatformNotSupportedException();
}
public sealed class WslDeviceService(WindowsPaths paths, AuthStore auth)
{
    public bool HasPendingBootstrap => false;
    public Task<IReadOnlyList<string>> RecoverPendingBootstrapTransactionsAsync(bool startStopped = false, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<WslDistribution>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WslDistribution>>([]);
    public Task<IReadOnlyList<DeviceStatus>> FetchStatusesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DeviceStatus>>([]);
    public Task<DeviceTokenUsageSummary> FetchTokenUsageAsync(WslDeviceConfiguration device, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
    public IAccountTarget CreateAccountTarget(WslDeviceConfiguration device) => throw new PlatformNotSupportedException();
    public IAccountTarget CreateLogoutTarget(WslDeviceConfiguration device, int removedProfileId, int fallbackProfileId) => throw new PlatformNotSupportedException();
    public Task SyncAuthAsync(WslDeviceConfiguration device, int profileId, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
}
