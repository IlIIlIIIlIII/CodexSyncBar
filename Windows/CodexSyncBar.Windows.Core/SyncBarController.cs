using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexSyncBar.Windows.Core;

/// <summary>The app owns one instance; tray and the widget pipe use the same mutation path.</summary>
public sealed record ControllerRecoveryStatus(
    IReadOnlyList<string> PendingBootstrapOperations,
    LogoutRecoveryResult Logout,
    IReadOnlyList<Guid> PendingSecretCleanup,
    IReadOnlyList<int> PendingBrowserCleanup);

public sealed class SyncBarController : IDisposable
{
    private readonly WindowsPaths _paths;
    private readonly ConfigurationStore _configurationStore;
    private readonly AuthStore _auth;
    private readonly LocalSwitchService _local;
    private readonly SshDeviceService _ssh;
    private readonly WslDeviceService _wsl;
    private readonly UsageService _usage;
    private readonly AccountSwitchCoordinator _coordinator;
    private readonly Func<AppConfiguration, IReadOnlyList<IAccountTarget>>? _targetFactory;
    private readonly Func<AppConfiguration, CancellationToken, Task<IReadOnlyList<DashboardDevice>>>? _deviceQuery;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<int, DashboardUsage> _usageCache = [];
    private readonly Dictionary<int, UsageSnapshot> _snapshots = [];
    private IReadOnlyList<DashboardDevice> _devices = [];
    private SwitchOperation? _operation;
    private bool _switchRunning;
    private long _deviceGeneration;
    private long _deviceRefreshRequest;
    private Task? _background;
    private DateTimeOffset _lastUsageRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDeviceRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMaintenance = DateTimeOffset.MinValue;
    private string? _error;

    public SyncBarController(WindowsPaths paths, UsageService? usageService = null)
        : this(paths, usageService, null, null) { }

    internal SyncBarController(WindowsPaths paths, UsageService? usageService,
        Func<AppConfiguration, IReadOnlyList<IAccountTarget>>? targetFactory,
        Func<AppConfiguration, CancellationToken, Task<IReadOnlyList<DashboardDevice>>>? deviceQuery,
        Func<int, CancellationToken, Task<string>>? weeklySender = null)
    {
        _paths = paths;
        _usage = usageService ?? new UsageService();
        _configurationStore = new(paths);
        _auth = new(paths);
        _local = new(_auth, paths);
        _ssh = new(_auth, paths, _local);
        _wsl = new(paths, _auth);
        _coordinator = new(paths);
        _targetFactory = targetFactory;
        _deviceQuery = deviceQuery;
        var anchorSender = new WeeklyAnchorService(paths, _auth, new CodexAuthMaintenanceService(paths, _auth, _local));
        WeeklyAnchor = new(paths, weeklySender ?? anchorSender.SendAsync, canSend: () =>
        {
            lock (_gate) return !_switchRunning && _operation?.State != "recoveryRequired" && !_wsl.HasPendingBootstrap;
        });
        WeeklyAnchor.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _coordinator.Progress += operation =>
        {
            lock (_gate) { _operation = operation; _deviceGeneration++; _lastDeviceRefresh = DateTimeOffset.MinValue; }
            Changed?.Invoke(this, EventArgs.Empty);
        };
        var cachePath = CachePath;
        if (File.Exists(cachePath))
        {
            try
            {
                WindowsPathSafety.EnsureFile(cachePath, "대시보드 사용량 캐시");
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<int, DashboardUsage>>(File.ReadAllText(cachePath)) ?? [])
                    _usageCache[pair.Key] = pair.Value;
            }
            catch (JsonException) { /* A disposable usage cache must not block recovery. */ }
        }
    }

    public ControllerRecoveryStatus? RecoveryStatus { get; private set; }
    public WeeklyAnchorCoordinator WeeklyAnchor { get; }
    public event EventHandler? Changed;

    public UsageSnapshot? TryGetUsageSnapshot(int profileId)
    {
        lock (_gate)
        {
            if (_snapshots.TryGetValue(profileId, out var current)) return current;
            if (!_usageCache.TryGetValue(profileId, out var cached) || cached.UpdatedAt == DateTimeOffset.MinValue) return null;
            return new UsageSnapshot(profileId, "", "Codex", cached.Session, cached.Weekly,
                null, false, cached.ResetCredits, cached.ResetCreditExpirations, cached.UpdatedAt);
        }
    }

    public string? GetUsageError(int profileId)
    {
        lock (_gate) return _usageCache.GetValueOrDefault(profileId)?.Error;
    }

    private string CachePath => Path.Combine(_paths.StateRoot, "dashboard-usage.json");

    public async Task InitializeAsync(CancellationToken cancellationToken = default, bool recoverStoppedWsl = false)
    {
        using (await ControllerMutationLock.AcquireAsync(_paths, cancellationToken: cancellationToken))
        {
            var configuration = _configurationStore.LoadOrCreate();
            new LoginTransactionStore(_paths).Recover(_auth);
            var pendingRefreshes = new CodexAuthMaintenanceService(_paths, _auth, _local).RecoverPendingRefreshes();
            new DeviceActivationTransactionStore(_paths).Recover(configuration, _configurationStore);
            var sshBootstrap = await _ssh.RecoverPendingBootstrapTransactionsAsync(configuration, cancellationToken);
            var wslBootstrap = await _wsl.RecoverPendingBootstrapTransactionsAsync(recoverStoppedWsl, cancellationToken);
            IReadOnlyList<string> bootstrap = sshBootstrap.Concat(wslBootstrap.Select(name => "WSL: " + name))
                .Concat(pendingRefreshes.Select(name => "인증 갱신: " + name)).ToArray();
            var logout = await _ssh.RecoverPendingLogoutsAsync(configuration, cancellationToken);
            var secrets = _ssh.RecoverPendingSecretCleanup(configuration);
            var browser = new BrowserLoginService(_paths).RecoverPendingProfiles();
            foreach (var profileId in logout.CompletedProfileIds)
                if (configuration.Accounts.Any(account => account.Id == profileId))
                    _configurationStore.MarkAccountLoggedOut(configuration, profileId);
            RecoveryStatus = new(bootstrap, logout, secrets, browser);
            _configurationStore.ReconcilePendingAccounts(configuration, _auth);
            _configurationStore.DiscoverExistingAccounts(configuration, _auth);
            var removed = _coordinator.ReadLogoutProfileId();
            _operation = await _coordinator.RecoverAsync(removed is { } profile
                ? CreateLogoutTargets(configuration, profile, _coordinator.ReadOperation()!.ProfileId)
                : CreateTargets(configuration));
            _coordinator.ReconcileCompletedLogout(_configurationStore, configuration, _auth);
            _auth.ProtectLegacyProfiles();
            if (bootstrap.Count > 0 || logout.PendingOperations.Count > 0)
            {
                _operation = new SwitchOperation { Id = "startup-recovery", State = "recoveryRequired", Message = "이전 장치 작업의 복구를 완료해 주세요." };
                _error = "연결할 수 없는 장치의 복구가 필요합니다.";
            }
        }
        lock (_gate) { _deviceGeneration++; _lastDeviceRefresh = DateTimeOffset.MinValue; }
        _background ??= RunBackgroundAsync(_lifetime.Token);
    }

    public async Task RetryRecoveryAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_switchRunning) throw new CodexSyncBarException("진행 중인 계정 전환을 먼저 완료해 주세요.");
        }
        await InitializeAsync(cancellationToken, recoverStoppedWsl: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = _configurationStore.LoadOrCreate();
        var wsl = new WslConfigurationStore(_paths).Load();
        var active = _local.GetActiveProfileId(configuration.Accounts);
        lock (_gate)
        {
            var accounts = configuration.Accounts.Select(account => new DashboardAccount(account.Id,
                string.IsNullOrWhiteSpace(account.CustomAlias) ? MaskEmail(account.Email) : account.Alias,
                MaskEmail(account.Email), account.NeedsLogin || account.IsPending,
                _usageCache.GetValueOrDefault(account.Id))).ToArray();
            var devices = new List<DashboardDevice> { new("windows", "이 Windows PC", "windows", active, true, _local.ReconnectionDetail) };
            foreach (var device in configuration.Devices.Where(device => device.Enabled))
                devices.Add(_devices.FirstOrDefault(status => status.Id == "ssh:" + device.Id)
                    ?? new("ssh:" + device.Id, device.DisplayLabel, "ssh", null, false, "상태 확인 전"));
            foreach (var device in wsl.Where(device => device.Enabled))
                devices.Add(_devices.FirstOrDefault(status => status.Id == device.Id)
                    ?? new(device.Id, device.Distribution, "wsl", null, false, "상태 확인 전"));
            return Task.FromResult(new DashboardSnapshot
            {
                ConfigurationRevision = ComputeRevision(configuration, wsl), Accounts = accounts, Devices = devices,
                ActiveProfileId = active, IsBusy = _switchRunning, Operation = _operation, Error = _error,
                UpdatedAt = _lastUsageRefresh == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : _lastUsageRefresh,
            });
        }
    }

    public async Task<DashboardSnapshot> RefreshUsageAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var configuration = _configurationStore.LoadOrCreate();
            lock (_gate) _error = null;
            foreach (var account in configuration.Accounts.Where(account => !account.IsPending && !account.NeedsLogin))
            {
                try
                {
                    var credentials = _auth.ReadCredentials(account.Id);
                    UsageSnapshot usage;
                    try { usage = await _usage.FetchAsync(credentials, cancellationToken); }
                    catch (AuthenticationRequiredException)
                    {
                        using var mutation = await ControllerMutationLock.AcquireAsync(_paths, cancellationToken: cancellationToken);
                        lock (_gate)
                        {
                            if (_switchRunning || _operation?.State == "recoveryRequired" || _wsl.HasPendingBootstrap) throw;
                        }
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        deadline.CancelAfter(TimeSpan.FromMinutes(2));
                        var latest = _auth.ReadCredentials(account.Id);
                        if (latest.AccessToken != credentials.AccessToken || latest.AccountId != credentials.AccountId)
                        {
                            credentials = latest;
                            usage = await _usage.FetchAsync(credentials, deadline.Token);
                        }
                        else
                        {
                            var maintenance = new CodexAuthMaintenanceService(_paths, _auth, _local);
                            var refresh = await maintenance.RefreshAsync(account.Id, deadline.Token);
                            if (refresh.DidDefer) throw;
                            credentials = _auth.ReadCredentials(account.Id);
                            usage = await _usage.FetchAsync(credentials, deadline.Token);
                            // Distribution is done by the next maintenance pass under this same controller lock.
                            _lastMaintenance = DateTimeOffset.MinValue;
                        }
                    }
                    // A login/refresh may have completed while the network request was in flight.
                    var current = _auth.ReadCredentials(account.Id);
                    if (current.AccountId != credentials.AccountId || current.AccessToken != credentials.AccessToken) continue;
                    lock (_gate) { _usageCache[account.Id] = ToDashboardUsage(usage); _snapshots[account.Id] = usage; }
                    _ = EvaluateWeeklyAnchorAsync(usage);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        _error = "일부 계정 사용량을 갱신하지 못했습니다. 마지막 정상 값을 표시합니다.";
                        var cached = _usageCache.GetValueOrDefault(account.Id)
                            ?? new DashboardUsage(null, null, null, [], DateTimeOffset.MinValue);
                        _usageCache[account.Id] = cached with { Error = error is AuthenticationRequiredException ? "재로그인이 필요합니다." : "사용량 갱신 실패" };
                    }
                }
            }
            lock (_gate) _lastUsageRefresh = DateTimeOffset.UtcNow;
            SaveCache();
        }
        finally { _refreshGate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        return await GetSnapshotAsync(cancellationToken);
    }

    public async Task<SwitchOperation> SwitchAccountAsync(int profileId, string expectedConfigurationRevision,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        if (snapshot.ConfigurationRevision != expectedConfigurationRevision)
            throw new CodexSyncBarException("계정 또는 장치 설정이 변경되었습니다. 새로고침한 후 다시 적용해 주세요.");
        if (!snapshot.Accounts.Any(account => account.ProfileId == profileId && !account.NeedsLogin))
            throw new CodexSyncBarException("이 계정은 삭제되었거나 로그인이 필요합니다.");
        SwitchOperation operation;
        lock (_gate)
        {
            if (_switchRunning) throw new CodexSyncBarException("계정 전환이 이미 진행 중입니다.");
            if (_operation?.State == "recoveryRequired") throw new CodexSyncBarException("이전 계정 전환의 복구를 먼저 완료해 주세요.");
            _switchRunning = true;
            _deviceGeneration++;
            operation = new SwitchOperation { Id = Guid.NewGuid().ToString("N"), ProfileId = profileId, State = "queued", Message = "계정 전환을 준비하고 있습니다." };
            _operation = operation;
        }
        _ = ExecuteSwitchAsync(operation, expectedConfigurationRevision);
        Changed?.Invoke(this, EventArgs.Empty);
        return operation;
    }

    public async Task<SwitchOperation> LogoutAccountAsync(int profileId, int fallbackProfileId, CancellationToken cancellationToken = default)
    {
        if (profileId == fallbackProfileId) throw new CodexSyncBarException("다른 계정을 선택한 후 로그아웃해 주세요.");
        lock (_gate)
        {
            if (_switchRunning || _operation?.State == "recoveryRequired")
                throw new CodexSyncBarException("진행 중인 계정 전환 또는 복구를 먼저 완료해 주세요.");
            _switchRunning = true;
            _deviceGeneration++;
        }
        try
        {
            using var mutation = await ControllerMutationLock.AcquireAsync(_paths, cancellationToken: cancellationToken);
            var configuration = _configurationStore.LoadOrCreate();
            EnsureRefreshRecoveryComplete();
            if (_wsl.HasPendingBootstrap) throw new CodexSyncBarException("이전 WSL 설치 복구를 먼저 완료해 주세요.");
            _ = _auth.ReadCredentials(profileId);
            _ = _auth.ReadCredentials(fallbackProfileId);
            var result = await _coordinator.SwitchAsync(fallbackProfileId, CreateLogoutTargets(configuration, profileId, fallbackProfileId),
                cancellationToken: cancellationToken, removedProfileId: profileId);
            if (result.State == "completed")
            {
                _coordinator.ReconcileCompletedLogout(_configurationStore, configuration, _auth);
                lock (_gate) { _usageCache.Remove(profileId); _snapshots.Remove(profileId); }
                SaveCache();
            }
            return result;
        }
        finally
        {
            lock (_gate) { _switchRunning = false; _deviceGeneration++; _lastDeviceRefresh = DateTimeOffset.MinValue; }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task<SwitchOperation?> GetOperationStatusAsync(string operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_operation?.Id == operationId ? _operation : null);
    }

    private async Task ExecuteSwitchAsync(SwitchOperation operation, string revision)
    {
        try
        {
            using var mutation = await ControllerMutationLock.AcquireAsync(_paths, TimeSpan.FromSeconds(30), _lifetime.Token);
            var configuration = _configurationStore.LoadOrCreate();
            EnsureRefreshRecoveryComplete();
            if (_wsl.HasPendingBootstrap) throw new CodexSyncBarException("이전 WSL 설치 복구를 먼저 완료해 주세요.");
            if (ComputeRevision(configuration, new WslConfigurationStore(_paths).Load()) != revision)
                throw new CodexSyncBarException("계정 또는 장치 설정이 변경되었습니다. 새로고침한 후 다시 적용해 주세요.");
            _ = _auth.ReadCredentials(operation.ProfileId);
            await _coordinator.SwitchAsync(operation.ProfileId, CreateTargets(configuration), operation.Id, _lifetime.Token);
        }
        catch (Exception)
        {
            lock (_gate)
                if (_operation?.State != "recoveryRequired")
                    _operation = operation with { State = "failed", Message = "계정 전환을 시작하지 못했습니다. 설정과 계정 상태를 확인해 주세요.", FinishedAt = DateTimeOffset.UtcNow };
        }
        finally
        {
            lock (_gate) { _switchRunning = false; _deviceGeneration++; _lastDeviceRefresh = DateTimeOffset.MinValue; }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RefreshDevicesAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        long request;
        lock (_gate)
        {
            if (_switchRunning) return;
            generation = _deviceGeneration;
            request = ++_deviceRefreshRequest;
        }
        var configuration = _configurationStore.LoadOrCreate();
        var revision = ComputeRevision(configuration, new WslConfigurationStore(_paths).Load());
        var devices = await (_deviceQuery is null ? QueryDevicesAsync(configuration, cancellationToken) : _deviceQuery(configuration, cancellationToken));
        if (ComputeRevision(_configurationStore.LoadOrCreate(), new WslConfigurationStore(_paths).Load()) != revision) return;
        lock (_gate)
        {
            if (_switchRunning || generation != _deviceGeneration || request != _deviceRefreshRequest) return;
            _devices = devices;
            _lastDeviceRefresh = DateTimeOffset.UtcNow;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<IReadOnlyList<DashboardDevice>> QueryDevicesAsync(AppConfiguration configuration, CancellationToken cancellationToken)
    {
        _local.RefreshClientStatus();
        var ssh = await _ssh.FetchStatusesAsync(configuration, _local.GetActiveProfileId(configuration.Accounts), cancellationToken);
        var wsl = await _wsl.FetchStatusesAsync(cancellationToken);
        return ssh.Where(status => status.Id != "windows")
            .Select(status => new DashboardDevice("ssh:" + status.Id, status.DisplayName, "ssh", status.ProfileId, status.IsReachable, status.IsReachable ? null : "장치 연결 또는 활성 계정을 확인해 주세요."))
            .Concat(wsl.Select(status => new DashboardDevice(status.Id, status.DisplayName, "wsl", status.ProfileId, status.IsReachable, status.IsReachable ? null : "장치 연결 또는 활성 계정을 확인해 주세요."))).ToArray();
    }

    private async Task EvaluateWeeklyAnchorAsync(UsageSnapshot usage)
    {
        try
        {
            lock (_gate) { if (_switchRunning || _operation?.State == "recoveryRequired" || _wsl.HasPendingBootstrap) return; }
            if (await WeeklyAnchor.EvaluateAsync(usage, token: _lifetime.Token))
                lock (_gate) _lastUsageRefresh = DateTimeOffset.MinValue;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { /* Durable per-account status is shown in weekly message settings. */ }
    }

    public async Task SetWeeklyAnchorEnabledAsync(int profileId, bool enabled)
    {
        await WeeklyAnchor.SetEnabledAsync(profileId, enabled, _lifetime.Token);
        if (enabled) await RefreshUsageAsync(_lifetime.Token);
    }

    public async Task<bool> SendWeeklyAnchorNowAsync(int profileId)
    {
        lock (_gate)
            if (_switchRunning || _operation?.State == "recoveryRequired" || _wsl.HasPendingBootstrap)
                throw new CodexSyncBarException("진행 중인 장치 작업 또는 복구를 먼저 완료해 주세요.");
        var credentials = _auth.ReadCredentials(profileId);
        var usage = await _usage.FetchAsync(credentials, _lifetime.Token);
        var sent = await WeeklyAnchor.EvaluateAsync(usage, manual: true, token: _lifetime.Token);
        if (sent) await RefreshUsageAsync(_lifetime.Token);
        return sent;
    }

    private async Task RunBackgroundAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                if (DateTimeOffset.UtcNow - _lastUsageRefresh >= TimeSpan.FromMinutes(5)) await RefreshUsageAsync(cancellationToken);
                if (DateTimeOffset.UtcNow - _lastDeviceRefresh >= TimeSpan.FromMinutes(30)) await RefreshDevicesAsync(cancellationToken);
                if (DateTimeOffset.UtcNow - _lastMaintenance >= TimeSpan.FromHours(1)) await MaintainAuthAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception) { lock (_gate) _error = "백그라운드 갱신에 실패했습니다. 다음 주기에 다시 시도합니다."; }
        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task MaintainAuthAsync(CancellationToken cancellationToken)
    {
        lock (_gate) { if (_switchRunning || _operation?.State == "recoveryRequired" || _wsl.HasPendingBootstrap) return; }
        using var mutation = await ControllerMutationLock.AcquireAsync(_paths, cancellationToken: cancellationToken);
        EnsureRefreshRecoveryComplete();
        var configuration = _configurationStore.LoadOrCreate();
        var maintenance = new CodexAuthMaintenanceService(_paths, _auth, _local);
        var running = await _wsl.DiscoverAsync(cancellationToken);
        foreach (var account in configuration.Accounts.Where(account => !account.IsPending && !account.NeedsLogin))
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromMinutes(2));
                var result = await maintenance.RefreshIfNeededAsync(account.Id, TimeSpan.FromHours(2), deadline.Token);
                if (result.DidDefer) continue;
                await _ssh.SyncProfileAsync(configuration, account.Id, deadline.Token);
                foreach (var device in new WslConfigurationStore(_paths).Load().Where(device => device.Enabled
                    && running.Any(state => state.Name == device.Distribution && state.IsRunning)))
                    await _wsl.SyncAuthAsync(device, account.Id, deadline.Token);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                lock (_gate) _error = "일부 계정 인증 유지에 실패했습니다. 계정 상태를 확인해 주세요.";
            }
        }
        _lastMaintenance = DateTimeOffset.UtcNow;
    }

    private void EnsureRefreshRecoveryComplete()
    {
        if (new CodexAuthMaintenanceService(_paths, _auth, _local).RecoverPendingRefreshes().Count == 0) return;
        lock (_gate) _operation = new SwitchOperation
        {
            Id = "refresh-recovery", State = "recoveryRequired", Message = "이전 인증 갱신의 복구를 완료해 주세요.",
        };
        throw new CodexSyncBarException("이전 인증 갱신의 복구를 먼저 완료해 주세요.");
    }

    private IReadOnlyList<IAccountTarget> CreateTargets(AppConfiguration configuration) =>
        _targetFactory?.Invoke(configuration) ?? configuration.Devices.Where(device => device.Enabled).Select(_ssh.CreateAccountTarget)
            .Concat(new WslConfigurationStore(_paths).Load().Where(device => device.Enabled).Select(_wsl.CreateAccountTarget))
            .Append(new WindowsAccountTarget(_auth, _local, _paths)).ToArray();

    private IReadOnlyList<IAccountTarget> CreateLogoutTargets(AppConfiguration configuration, int removedProfileId, int fallbackProfileId) =>
        configuration.Devices.Where(device => device.Enabled).Select(device => _ssh.CreateLogoutTarget(device, removedProfileId, fallbackProfileId))
            .Concat(new WslConfigurationStore(_paths).Load().Where(device => device.Enabled)
                .Select(device => _wsl.CreateLogoutTarget(device, removedProfileId, fallbackProfileId)))
            .Append(new WindowsLogoutTarget(_auth, _local, _paths, removedProfileId)).ToArray();

    public static string ComputeRevision(AppConfiguration configuration, IReadOnlyList<WslDeviceConfiguration> wsl) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { configuration, wsl }))));

    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return "로그인 필요";
        return email[..1] + "***" + email[at..];
    }

    private static DashboardUsage ToDashboardUsage(UsageSnapshot usage) => new(usage.Session, usage.Weekly,
        usage.ResetCredits, usage.ResetCreditExpirations, usage.UpdatedAt);

    private void SaveCache()
    {
        _paths.EnsureDirectories();
        WindowsPathSafety.EnsureFile(CachePath, "대시보드 사용량 캐시");
        var temporary = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_usageCache);
            File.WriteAllText(temporary, json);
            File.Move(temporary, CachePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose() => _lifetime.Cancel();
}
