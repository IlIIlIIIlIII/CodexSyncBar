using System.Collections.ObjectModel;
using CodexSyncBar.Windows.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace CodexSyncBar_Windows;

public sealed partial class MainPage : Page
{
    private readonly WindowsPaths _paths;
    private readonly ConfigurationStore _configurationStore;
    private readonly AuthStore _authStore;
    private readonly UsageService _usageService;
    private readonly LocalSwitchService _localSwitchService;
    private readonly BrowserLoginService _browserLoginService;
    private readonly CodexLoginService _codexLoginService;
    private readonly CodexAuthMaintenanceService _authMaintenanceService;
    private readonly SshDeviceService _sshDeviceService;
    private readonly TokenUsageService _tokenUsageService;
    private readonly CursorBridgeService _cursorBridgeService;
    private readonly CodexConfigService _codexConfigService;
    private readonly CursorProviderService _cursorProviderService;
    private readonly CursorApiKeyStore _cursorApiKeyStore;
    private readonly UsageDisplayPreferencesStore _usageDisplayStore;
    private readonly SelectedProfileStore _selectedProfileStore;
    private readonly WeeklyAnchorStore _weeklyAnchorStore;
    private readonly WeeklyAnchorService _weeklyAnchorService;
    private readonly AuthMaintenanceStateStore _authMaintenanceStateStore;
    private readonly DeviceActivationTransactionStore _deviceActivationTransactions;
    private readonly LoginTransactionStore _loginTransactions;
    private readonly ObservableCollection<DeviceRow> _deviceRows = [];
    private readonly ObservableCollection<TokenUsageRow> _tokenUsageRows = [];
    private CursorModelCatalog? _cursorModelCatalog;

    private AppConfiguration _configuration = new();
    private UsageDisplayPreferences _usageDisplayPreferences = new();
    private MenuBarUsagePreferences _menuBarUsagePreferences = new();
    private UsageSnapshot? _lastUsageSnapshot;
    private readonly Dictionary<int, UsageSnapshot> _usageSnapshots = [];
    private readonly Dictionary<int, string> _usageErrors = [];
    private HashSet<int> _pendingBrowserCleanup = [];
    private WeeklyAnchorState _weeklyAnchorState = new();
    private AuthMaintenanceState _authMaintenanceState = new();
    private readonly HashSet<int> _weeklyAnchorRunning = [];
    private readonly TaskCompletionSource<bool> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int? _selectedProfileId;
    private CancellationTokenSource? _usageCancellation;
    private string? _selectedDeviceId;
    private DispatcherTimer? _usageTimer;
    private DispatcherTimer? _deviceTimer;
    private DispatcherTimer? _maintenanceTimer;
    private DispatcherTimer? _fullSyncTimer;
    private DispatcherTimer? _resetCreditsTimer;
    private bool _isBusy;
    private bool _hasLoaded;
    private bool _configurationRecoveryNeeded;
    private int _activeUsageRefreshes;
    private bool _updatingCursorModelPicker;
    private CancellationTokenSource? _loginCancellation;
    private int? _lastLoginProfileId;
    private bool _lastLoginReplaceExisting;
    private bool _suppressAccountSelectionChange;
    private string _bannerMessage = "준비 중…";
    private bool _bannerIsError;

    public event EventHandler? TrayStateChanged;

    public MainPage()
    {
        InitializeComponent();

        _paths = new WindowsPaths();
        _configurationStore = new ConfigurationStore(_paths);
        _authStore = new AuthStore(_paths);
        _usageService = new UsageService();
        _localSwitchService = new LocalSwitchService(_authStore, _paths);
        _browserLoginService = new BrowserLoginService(_paths);
        _loginTransactions = new LoginTransactionStore(_paths);
        _codexLoginService = new CodexLoginService(
            _paths,
            _authStore,
            _browserLoginService,
            _loginTransactions);
        _authMaintenanceService = new CodexAuthMaintenanceService(
            _paths,
            _authStore,
            _localSwitchService);
        _sshDeviceService = new SshDeviceService(_authStore, _paths, _localSwitchService);
        _tokenUsageService = new TokenUsageService(_paths);
        _cursorBridgeService = new CursorBridgeService(_paths);
        _cursorBridgeService.OnUnexpectedStatusChange = status =>
        {
            DispatcherQueue.TryEnqueue(() => RenderCursorStatus(status));
        };
        _codexConfigService = new CodexConfigService(_paths);
        _cursorProviderService = new CursorProviderService(
            _paths,
            _cursorBridgeService,
            _codexConfigService);
        _cursorApiKeyStore = new CursorApiKeyStore(new WindowsSecretStore(_paths));
        _usageDisplayStore = new UsageDisplayPreferencesStore(_paths);
        _selectedProfileStore = new SelectedProfileStore(_paths);
        _weeklyAnchorStore = new WeeklyAnchorStore(_paths);
        _weeklyAnchorService = new WeeklyAnchorService(
            _paths,
            _authStore,
            _authMaintenanceService);
        _authMaintenanceStateStore = new AuthMaintenanceStateStore(_paths);
        _deviceActivationTransactions = new DeviceActivationTransactionStore(_paths);
        DevicesList.ItemsSource = _deviceRows;
        TokenUsageDevicesList.ItemsSource = _tokenUsageRows;
        Loaded += MainPage_Loaded;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainPage_Loaded;
        try
        {
            await LoadAsync();
        }
        finally
        {
            _ready.TrySetResult(true);
        }
    }

    private async Task LoadAsync(bool refreshUsage = true)
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(true);
        var configurationPhaseCompleted = false;
        try
        {
            _configuration = _configurationStore.LoadOrCreate();
            LogoutRecoveryResult pendingLogoutRecovery;
            IReadOnlyList<Guid> pendingSecretCleanup;
            IReadOnlyList<string> pendingBootstrapRecovery;
            using (var recoveryLock = await ControllerMutationLock.AcquireAsync(_paths))
            {
                _loginTransactions.Recover(_authStore);
                _deviceActivationTransactions.Recover(_configuration, _configurationStore);
                pendingBootstrapRecovery =
                    await _sshDeviceService.RecoverPendingBootstrapTransactionsAsync(_configuration);
                pendingLogoutRecovery = await _sshDeviceService.RecoverPendingLogoutsAsync(_configuration);
                pendingSecretCleanup = _sshDeviceService.RecoverPendingSecretCleanup(_configuration);
                foreach (var profileId in pendingLogoutRecovery.CompletedProfileIds)
                {
                    if (_configuration.Accounts.Any(account => account.Id == profileId))
                    {
                        _configurationStore.RemoveAccount(_configuration, profileId);
                    }
                }
            }
            var pendingBrowserCleanup = _browserLoginService.RecoverPendingProfiles();
            _pendingBrowserCleanup = pendingBrowserCleanup.ToHashSet();
            UpdateBrowserCleanupActions();
            _configurationStore.ReconcilePendingAccounts(_configuration, _authStore);
            _configurationStore.DiscoverExistingAccounts(_configuration, _authStore);
            _usageDisplayPreferences = _usageDisplayStore.LoadUsagePreferences();
            _menuBarUsagePreferences = _usageDisplayStore.LoadMenuPreferences();
            _weeklyAnchorState = _weeklyAnchorStore.Load();
            _authMaintenanceState = _authMaintenanceStateStore.Load();
            var activeProfileId = _localSwitchService.GetActiveProfileId(_configuration.Accounts);
            var persistedProfileId = _selectedProfileId ?? _selectedProfileStore.Load();
            var selectedId = persistedProfileId.HasValue
                && _configuration.Accounts.Any(account => account.Id == persistedProfileId)
                ? persistedProfileId
                : activeProfileId ?? _configuration.Accounts.FirstOrDefault()?.Id;
            _configurationRecoveryNeeded = false;
            configurationPhaseCompleted = true;
            UpdateAccountsView(selectedId);
            _hasLoaded = true;
            await RefreshDevicesAsync();
            if (refreshUsage)
            {
                await RefreshAllUsageAsync();
            }

            await RefreshTokenUsageAsync();
            await RefreshCursorAsync(autoStart: true);
            await MaintainAuthAsync(reportBanner: false);

                SetBanner(
                    pendingLogoutRecovery.PendingOperations.Count == 0
                    && pendingBrowserCleanup.Count == 0
                    && pendingSecretCleanup.Count == 0
                    && pendingBootstrapRecovery.Count == 0
                    ? "준비되었습니다."
                    : $"복구 대기 작업이 있습니다. 원격 부트스트랩: {string.Join(", ", pendingBootstrapRecovery)} · 로그아웃: {string.Join(", ", pendingLogoutRecovery.PendingOperations)} · Chrome: {string.Join(", ", pendingBrowserCleanup)} · SSH 비밀: {string.Join(", ", pendingSecretCleanup)}",
                isError: pendingLogoutRecovery.PendingOperations.Count > 0
                    || pendingBrowserCleanup.Count > 0
                    || pendingSecretCleanup.Count > 0
                    || pendingBootstrapRecovery.Count > 0);
            StartPolling();
        }
        catch (Exception error)
        {
            if (!configurationPhaseCompleted)
            {
                _configurationRecoveryNeeded = true;
            }

            SetBanner(error.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    public async Task RefreshFromTrayAsync()
    {
        if (!_hasLoaded)
        {
            await LoadAsync();
            return;
        }

        await RefreshUsageIfStaleAsync();
    }

    internal TrayPopoverSnapshot CreateTrayPopoverSnapshot()
    {
        int? activeProfileId;
        try
        {
            activeProfileId = _localSwitchService.GetActiveProfileId(_configuration.Accounts);
        }
        catch
        {
            activeProfileId = null;
        }

        var selected = _configuration.Accounts.FirstOrDefault(account => account.Id == _selectedProfileId)
            ?? _configuration.Accounts.FirstOrDefault(account => account.Id == activeProfileId)
            ?? _configuration.Accounts.FirstOrDefault();
        var selectedSnapshot = selected is null
            ? null
            : _usageSnapshots.GetValueOrDefault(selected.Id);
        var accounts = _configuration.Accounts.Select(account =>
        {
            _usageErrors.TryGetValue(account.Id, out var error);
            var accountSnapshot = _usageSnapshots.GetValueOrDefault(account.Id);
            var status = account.IsPending
                ? "로그인 필요"
                : account.NeedsLogin
                    ? "재로그인 필요"
                    : !string.IsNullOrWhiteSpace(error)
                        ? "확인 필요"
                        : account.Id == activeProfileId
                            ? "현재 사용 중"
                            : "사용 가능";
            return new TrayAccountSnapshot(
                account.Id,
                account.Alias,
                account.Email,
                account.ShortName,
                account.Id == selected?.Id,
                account.Id == activeProfileId,
                account.NeedsLogin,
                account.IsPending,
                status,
                accountSnapshot?.MenuRemainingPercent is { } remaining ? $"{remaining}%" : "—");
        }).ToArray();
        var devices = _deviceRows.Select(device =>
        {
            var profile = device.ProfileId is { } profileId
                ? _configuration.Accounts.FirstOrDefault(account => account.Id == profileId)
                : null;
            return new TrayDeviceSnapshot(
                device.Id,
                device.DisplayName,
                device.StateText,
                profile?.Alias ?? "계정 확인 전",
                device.IsReachable);
        }).ToArray();
        var visibleUsageItems = Enum.GetValues<UsageDisplayItem>()
            .Where(_usageDisplayPreferences.IsVisible)
            .ToArray();
        var creditsText = selectedSnapshot is null
            ? "추가 크레딧 —"
            : selectedSnapshot.UnlimitedCredits
                ? "추가 크레딧 무제한"
                : selectedSnapshot.CreditBalance.HasValue
                    ? $"추가 크레딧 {selectedSnapshot.CreditBalance.Value:0.##}"
                    : "추가 크레딧 확인되지 않음";
        if (selectedSnapshot?.ResetCredits is { } resetCredits)
        {
            creditsText += $" · 초기화권 {resetCredits}개";
        }

        var resetCreditsText = selectedSnapshot is null
            ? string.Empty
            : UsageFormatting.CompactResetCreditExpiryDescription(
                selectedSnapshot.ResetCreditExpirations,
                DateTimeOffset.UtcNow)
                ?? (selectedSnapshot.ResetCredits.HasValue ? "만료 정보 없음" : string.Empty);
        var selectedError = selected is not null
            && _usageErrors.TryGetValue(selected.Id, out var usageError)
                ? usageError
                : null;
        var authenticationText = selected is null
            ? "계정 없음"
            : selected.IsPending
                ? "로그인 필요"
                : selected.NeedsLogin
                    ? "재로그인 필요"
                    : selectedError is not null
                        ? "확인 필요"
                        : "인증 정상";
        var hasDeviceMismatch = _deviceRows.Any(device =>
            !device.IsReachable || device.ProfileId != activeProfileId);
        var canApply = selected is not null
            && !selected.IsPending
            && !selected.NeedsLogin
            && !_configurationRecoveryNeeded
            && !_isBusy
            && _authStore.ProfileArtifactExists(selected.Id);
        var banner = string.Equals(_bannerMessage, "준비되었습니다.", StringComparison.Ordinal)
            ? null
            : _bannerMessage;

        return new TrayPopoverSnapshot(
            accounts,
            devices,
            selected?.Id,
            activeProfileId,
            selected?.Alias ?? "계정 없음",
            selected?.Email ?? string.Empty,
            selected?.ShortName ?? "?",
            selectedSnapshot?.Plan ?? "Codex",
            authenticationText,
            selectedSnapshot,
            visibleUsageItems,
            selectedError,
            creditsText,
            resetCreditsText,
            ((App)Application.Current).TrayIcon?.CurrentTitle ?? "Codex SyncBar",
            banner,
            _bannerIsError,
            _isBusy || _activeUsageRefreshes > 0 || _loginCancellation is not null,
            canApply,
            hasDeviceMismatch);
    }

    internal async Task SelectFromTrayAsync(int profileId)
    {
        await _ready.Task;
        if (_isBusy)
        {
            return;
        }

        var account = _configuration.Accounts.FirstOrDefault(item => item.Id == profileId);
        if (account is null)
        {
            SetBanner($"계정 {profileId}를 찾지 못했습니다.", isError: true);
            return;
        }

        SelectAccountFromTray(account);
        await LoadUsageAsync(account.Id);
    }

    internal async Task RefreshTrayPopoverAsync()
    {
        await _ready.Task;
        if (_isBusy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await RefreshAllUsageAsync();
            await RefreshDevicesAsync();
            SetBanner("사용량과 장치 상태를 새로고침했습니다.", isError: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    internal async Task ApplyFromTrayAsync(int profileId)
    {
        await _ready.Task;
        if (_isBusy)
        {
            return;
        }

        var account = _configuration.Accounts.FirstOrDefault(item => item.Id == profileId);
        if (account is null)
        {
            SetBanner($"계정 {profileId}를 찾지 못했습니다.", isError: true);
            return;
        }

        SelectAccountFromTray(account);
        await ApplyAccountAsync(account);
    }

    private void SelectAccountFromTray(AccountProfile account)
    {
        _selectedProfileId = account.Id;
        _selectedProfileStore.Save(account.Id);
        _suppressAccountSelectionChange = true;
        try
        {
            AccountsList.SelectedItem = account;
        }
        finally
        {
            _suppressAccountSelectionChange = false;
        }

        UpdateSelectedAccount(account);
    }

    public async Task BeginLoginForProfileAsync(int profileId)
    {
        await _ready.Task;
        var account = _configuration.Accounts.FirstOrDefault(item => item.Id == profileId);
        if (account is null)
        {
            SetBanner($"계정 {profileId}를 찾지 못했습니다.", isError: true);
            return;
        }

        _selectedProfileId = account.Id;
        AccountsList.SelectedItem = account;
        UpdateSelectedAccount(account);
        await RunLoginAsync(account, replaceExisting: !account.IsPending);
    }

    public async Task ShutdownAsync()
    {
        _usageTimer?.Stop();
        _deviceTimer?.Stop();
        _maintenanceTimer?.Stop();
        _fullSyncTimer?.Stop();
        _resetCreditsTimer?.Stop();
        _usageCancellation?.Cancel();
        _loginCancellation?.Cancel();
        if (_lastLoginProfileId is { } profileId)
        {
            _browserLoginService.CloseLoginWindow(profileId);
        }

        await _cursorBridgeService.StopAsync();
    }

    public async Task RefreshUsageIfStaleAsync(
        TimeSpan? interval = null)
    {
        if (_isBusy)
        {
            return;
        }

        var freshness = interval ?? TimeSpan.FromSeconds(30);
        var accounts = _configuration.Accounts
            .Where(account => !account.IsPending && !account.NeedsLogin)
            .ToArray();
        var allFresh = accounts.Length > 0
            && accounts.All(account => _usageSnapshots.TryGetValue(account.Id, out var snapshot)
                && DateTimeOffset.UtcNow - snapshot.UpdatedAt <= freshness);
        if (!allFresh)
        {
            await RefreshAllUsageAsync();
        }
    }

    private void StartPolling()
    {
        if (_usageTimer is not null)
        {
            return;
        }

        _usageTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _usageTimer.Tick += async (_, _) =>
        {
            if (_isBusy)
            {
                return;
            }

            await RefreshAllUsageAsync();
        };
        _usageTimer.Start();

        _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _deviceTimer.Tick += async (_, _) =>
        {
            if (_isBusy)
            {
                return;
            }

            await RefreshDevicesAsync();
            await RefreshTokenUsageAsync();
        };
        _deviceTimer.Start();

        _maintenanceTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _maintenanceTimer.Tick += async (_, _) =>
        {
            if (_isBusy)
            {
                return;
            }

            try
            {
                _configurationStore.ReconcilePendingAccounts(_configuration, _authStore);
                UpdateAccountsView(_selectedProfileId);
                await MaintainAuthAsync();
            }
            catch (Exception error)
            {
                SetBanner($"백그라운드 복구 확인에 실패했습니다: {error.Message}", isError: true);
            }
        };
        _maintenanceTimer.Start();

        _fullSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _fullSyncTimer.Tick += async (_, _) =>
        {
            if (_isBusy)
            {
                return;
            }

            await MaintainAuthAsync(forceFullSync: true);
        };
        _fullSyncTimer.Start();

        _resetCreditsTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _resetCreditsTimer.Tick += (_, _) =>
        {
            if (_lastUsageSnapshot is not null)
            {
                RenderUsage(_lastUsageSnapshot);
            }
        };
        _resetCreditsTimer.Start();
    }

    private async Task MaintainAuthAsync(
        bool forceFullSync = false,
        bool reportBanner = true)
    {
        if (_configuration.Accounts.Count == 0)
        {
            return;
        }

        var failures = new List<string>();
        var refreshed = 0;
        var deferred = 0;
        try
        {
            using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            foreach (var account in _configuration.Accounts.Where(item =>
                !item.IsPending && !item.NeedsLogin))
            {
                try
                {
                    var result = await _authMaintenanceService.RefreshIfNeededAsync(
                        account.Id,
                        TimeSpan.FromDays(3));
                    if (result.DidRefresh)
                    {
                        refreshed++;
                        failures.AddRange(
                            await _sshDeviceService.SyncProfileAsync(_configuration, account.Id));
                    }
                    else if (result.DidDefer)
                    {
                        deferred++;
                    }
                }
                catch (Exception error) when (
                    error is CodexSyncBarException
                    or AuthenticationRequiredException
                    or IOException)
                {
                    if (error is AuthenticationRequiredException)
                    {
                        MarkAccountNeedsLogin(account.Id);
                    }

                    failures.Add($"계정 {account.Id}: {error.Message}");
                }
            }

            var fullSyncDue = forceFullSync
                || _authMaintenanceState.LastFullSyncAt is not { } lastSync
                || DateTimeOffset.UtcNow - lastSync >= TimeSpan.FromHours(6);
            if (fullSyncDue)
            {
                try
                {
                    var syncFailures = await _sshDeviceService.SyncAllProfilesAsync(_configuration);
                    failures.AddRange(syncFailures);
                    if (syncFailures.Count == 0)
                    {
                        _authMaintenanceState.LastFullSyncAt = DateTimeOffset.UtcNow;
                        _authMaintenanceStateStore.Save(_authMaintenanceState);
                    }
                }
                catch (Exception error) when (error is CodexSyncBarException or IOException)
                {
                    failures.Add(error.Message);
                }
            }
        }
        catch (Exception error) when (error is CodexSyncBarException or IOException)
        {
            failures.Add(error.Message);
        }

        if (refreshed > 0 && _selectedProfileId is { } selectedProfileId)
        {
            await RefreshAllUsageAsync();
            await RefreshDevicesAsync();
        }

        if (!reportBanner)
        {
            return;
        }

        if (failures.Count == 0)
        {
            SetBanner(
                deferred > 0
                    ? $"Codex 프로세스 사용 중인 계정 {deferred}개는 인증 갱신을 다음 점검으로 미뤘습니다."
                    : refreshed == 0
                        ? "인증 상태와 장치 동기화를 확인했습니다."
                    : $"인증 {refreshed}개를 갱신하고 장치 상태를 확인했습니다.",
                isError: false);
        }
        else
        {
            SetBanner(
                $"인증 자동 갱신 또는 전체 장치 동기화가 일부 보류되었습니다: {string.Join(" · ", failures)}",
                isError: true);
        }
    }

    private void UpdateAccountsView(int? selectedId)
    {
        _selectedProfileId = selectedId;
        if (selectedId is { } persistedId)
        {
            _selectedProfileStore.Save(persistedId);
        }
        AccountCountText.Text = $"{_configuration.Accounts.Count}개";
        AccountsList.ItemsSource = null;
        AccountsList.ItemsSource = _configuration.Accounts;
        var selected = _configuration.Accounts.FirstOrDefault(account => account.Id == selectedId)
            ?? _configuration.Accounts.FirstOrDefault();
        if (selected is not null)
        {
            _selectedProfileId = selected.Id;
            AccountsList.SelectedItem = selected;
            UpdateSelectedAccount(selected);
        }
        else
        {
            UpdateSelectedAccount(null);
        }
    }

    private async void AccountsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAccountSelectionChange)
        {
            return;
        }

        if (AccountsList.SelectedItem is not AccountProfile account)
        {
            return;
        }

        _selectedProfileId = account.Id;
        _selectedProfileStore.Save(account.Id);
        UpdateSelectedAccount(account);
        await LoadUsageAsync(account.Id);
    }

    private void UpdateSelectedAccount(AccountProfile? account)
    {
        var activeProfileId = _localSwitchService.GetActiveProfileId(_configuration.Accounts);
        var hasAccount = account is not null;
        SelectedAliasText.Text = account?.Alias ?? "계정을 선택해 주세요";
        SelectedEmailText.Text = account?.Email ?? string.Empty;
        SelectedProfilePathText.Text = account is null
            ? string.Empty
            : _authStore.ProfileAuthFile(account.Id);
        SelectedPlanText.Text = "Codex";
        ActiveBadgeText.Visibility = account is not null && activeProfileId == account.Id
            ? Visibility.Visible
            : Visibility.Collapsed;
        AuthStatusText.Text = account is null
            ? "인증 확인 전"
            : account.IsPending
                ? "로그인 필요"
                : account.NeedsLogin
                    ? "로그아웃됨 · 재로그인 필요"
                    : "인증 확인 중…";
        LoginButton.Content = account?.IsPending == true ? "로그인 열기" : "재로그인";
        ImportAuthButton.Content = account?.IsPending == true ? "인증 파일 가져오기" : "인증 파일 교체";
        DeleteAccountButton.IsEnabled = _configuration.Accounts.Count > 1
            && hasAccount
            && !_authStore.ProfileArtifactExists(account!.Id);
        LogoutAccountButton.IsEnabled = _configuration.Accounts.Count > 1
            && hasAccount
            && !account!.IsPending
            && !account.NeedsLogin
            && _authStore.ProfileArtifactExists(account.Id);
        RefreshSelectedButton.IsEnabled = hasAccount && !account!.IsPending && !account.NeedsLogin;
        LoginButton.IsEnabled = hasAccount;
        ImportAuthButton.IsEnabled = hasAccount;
        SyncButton.IsEnabled = hasAccount
            && account?.IsPending == false
            && account.NeedsLogin == false;
        EditAliasButtonIsEnabled(hasAccount);
        UpdateAccountOrderActions();
        UpdateLoginActions(account);
        ResetUsageView();
        NotifyTrayStateChanged();
    }

    private async Task LoadUsageAsync(int profileId, bool retriedAfterRefresh = false)
    {
        _usageCancellation?.Cancel();
        _usageCancellation?.Dispose();
        var cancellationSource = new CancellationTokenSource();
        _usageCancellation = cancellationSource;
        var cancellationToken = cancellationSource.Token;
        var account = _configuration.Accounts.FirstOrDefault(item => item.Id == profileId);
        if (account is null || account.IsPending || account.NeedsLogin)
        {
            ResetUsageView();
            AuthStatusText.Text = "로그인 필요";
            UsageStatusText.Text = "로그인 후 auth.json을 가져오면 사용량을 확인할 수 있습니다.";
            if (ReferenceEquals(_usageCancellation, cancellationSource))
            {
                _usageCancellation = null;
                cancellationSource.Dispose();
            }

            return;
        }

        _activeUsageRefreshes++;
        UpdateTrayTitle();
        try
        {
            if (_selectedProfileId == profileId)
            {
                AuthStatusText.Text = "인증 확인 중…";
                UsageStatusText.Text = "사용량을 가져오는 중…";
            }

            var credentials = _authStore.ReadCredentials(profileId);
            var snapshot = await _usageService.FetchAsync(credentials, cancellationToken);
            _usageSnapshots[profileId] = snapshot;
            _usageErrors.Remove(profileId);
            _ = EvaluateWeeklyAnchorAsync(snapshot);
            if (_selectedProfileId != profileId || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            AuthStatusText.Text = "인증 정상";
            SelectedPlanText.Text = snapshot.Plan;
            _lastUsageSnapshot = snapshot;
            RenderUsage(snapshot);
        }
        catch (OperationCanceledException)
        {
        }
        catch (AuthenticationRequiredException) when (!retriedAfterRefresh)
        {
            try
            {
                var maintenance = await _authMaintenanceService.RefreshAsync(profileId, cancellationToken);
                if (maintenance.DidDefer)
                {
                    throw new CodexSyncBarException(
                        "Codex 프로세스가 실행 중이어서 인증 갱신을 미뤘습니다. Codex를 닫은 뒤 다시 시도해 주세요.");
                }
                var syncFailures = await _sshDeviceService.SyncProfileAsync(
                    _configuration,
                    profileId,
                    cancellationToken);
                if (syncFailures.Count > 0 && _selectedProfileId == profileId)
                {
                    SetBanner(
                        $"인증은 갱신했지만 일부 SSH 장치 동기화가 보류되었습니다: {string.Join(" · ", syncFailures)}",
                        isError: true);
                }
                await LoadUsageAsync(profileId, retriedAfterRefresh: true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception refreshError) when (
                refreshError is CodexSyncBarException
                or AuthenticationRequiredException
                or IOException)
            {
                _usageErrors[profileId] = refreshError.Message;
                MarkAccountNeedsLoginIfCanonicalFailure(profileId, refreshError);
                if (_selectedProfileId == profileId)
                {
                    AuthStatusText.Text = "재로그인 필요";
                    UsageStatusText.Text = refreshError.Message;
                    SetBanner(refreshError.Message, isError: true);
                }
            }
        }
        catch (AuthenticationRequiredException error)
        {
            _usageErrors[profileId] = error.Message;
            MarkAccountNeedsLogin(profileId);
            if (_selectedProfileId == profileId)
            {
                AuthStatusText.Text = "재로그인 필요";
                UsageStatusText.Text = error.Message;
                SetBanner(error.Message, isError: true);
            }
        }
        catch (Exception error)
        {
            _usageErrors[profileId] = error.Message;
            if (_selectedProfileId == profileId)
            {
                AuthStatusText.Text = "확인 필요";
                UsageStatusText.Text = error.Message;
                SetBanner(error.Message, isError: true);
            }
        }
        finally
        {
            _activeUsageRefreshes = Math.Max(0, _activeUsageRefreshes - 1);
            if (ReferenceEquals(_usageCancellation, cancellationSource))
            {
                _usageCancellation = null;
                cancellationSource.Dispose();
            }

            UpdateTrayTitle();
        }
    }

    private async Task RefreshAllUsageAsync()
    {
        foreach (var account in _configuration.Accounts.Where(item =>
            !item.IsPending && !item.NeedsLogin).ToArray())
        {
            await LoadUsageAsync(account.Id);
        }

        if (_selectedProfileId is { } selectedProfileId
            && _usageSnapshots.TryGetValue(selectedProfileId, out var selectedSnapshot))
        {
            _lastUsageSnapshot = selectedSnapshot;
            RenderUsage(selectedSnapshot);
            AuthStatusText.Text = _usageErrors.TryGetValue(selectedProfileId, out var error)
                ? _configuration.Accounts.FirstOrDefault(item => item.Id == selectedProfileId)?.NeedsLogin == true
                    ? "재로그인 필요"
                    : "확인 필요"
                : _configuration.Accounts.FirstOrDefault(item => item.Id == selectedProfileId)?.NeedsLogin == true
                    ? "재로그인 필요"
                    : "인증 정상";
            UsageStatusText.Text = error ?? "사용량을 확인했습니다.";
        }
    }

    private async Task RefreshTokenUsageAsync()
    {
        TokenUsageStatusText.Text = "최근 30일 세션 로그를 집계하는 중…";
        try
        {
            var snapshot = await _tokenUsageService.FetchAsync(
                _configuration,
                _sshDeviceService);
            RenderTokenUsage(snapshot);
        }
        catch (OperationCanceledException)
        {
            TokenUsageStatusText.Text = "토큰 사용량 집계를 취소했습니다.";
        }
        catch (Exception error)
        {
            TokenUsageStatusText.Text = error.Message;
            SetBanner($"토큰 사용량을 집계하지 못했습니다: {error.Message}", isError: true);
        }
    }

    private void RenderTokenUsage(TokenUsageSnapshot snapshot)
    {
        var counts = snapshot.Counts;
        var requests = snapshot.Devices
            .Where(item => item.Summary is not null)
            .Sum(item => item.Summary!.Requests);
        TokenUsageCountText.Text =
            $"{TokenUsageFormatting.Tokens(counts.TotalTokens)} tokens · {requests:N0} requests";
        TokenUsageCostText.Text = snapshot.UnpricedTokens > 0
            ? $"예상 비용 {TokenUsageFormatting.Dollars(snapshot.EstimatedCostUsd)} · 가격표 없음 {TokenUsageFormatting.Tokens(snapshot.UnpricedTokens)}"
            : $"예상 비용 {TokenUsageFormatting.Dollars(snapshot.EstimatedCostUsd)}";
        TokenUsageDevicesText.Text =
            $"장치 {snapshot.ReachableDeviceCount}/{snapshot.TotalDeviceCount} 연결됨 · 입력 {TokenUsageFormatting.Tokens(counts.InputTokens)} · 출력 {TokenUsageFormatting.Tokens(counts.OutputTokens)}";
        TokenUsageUpdatedText.Text = $"갱신 {snapshot.CollectedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        var errors = snapshot.Devices
            .Where(item => !string.IsNullOrWhiteSpace(item.Error))
            .Select(item => $"{item.DisplayName}: {item.Error}")
            .ToArray();
        var pricingNotes = new List<string>();
        if (snapshot.PriorityPricedTokens > 0)
        {
            pricingNotes.Add("API Priority 단가 적용");
        }

        if (snapshot.UnpricedTokens > 0)
        {
            pricingNotes.Add($"미공개 가격 {TokenUsageFormatting.Tokens(snapshot.UnpricedTokens)}");
        }

        TokenUsageStatusText.Text = errors.Length == 0
            ? pricingNotes.Count == 0
                ? "Codex 세션 로그를 정상적으로 집계했습니다."
                : $"Codex 세션 로그를 정상적으로 집계했습니다. · {string.Join(" · ", pricingNotes)}"
            : string.Join(" · ", errors);

        _tokenUsageRows.Clear();
        foreach (var device in snapshot.Devices)
        {
            var status = _deviceRows.FirstOrDefault(item => item.Id == device.Id);
            var profile = status?.ProfileId is { } profileId
                ? _configuration.Accounts.FirstOrDefault(item => item.Id == profileId)
                : null;
            _tokenUsageRows.Add(new TokenUsageRow(device, profile));
        }
    }

    private async Task RefreshCursorAsync(bool autoStart)
    {
        try
        {
            var preferences = _cursorBridgeService.LoadPreferences();
            SetCursorModelDraft(preferences.Model);
            CursorPortTextBox.Text = preferences.Port.ToString();
            CursorAgentPathTextBox.Text = preferences.AgentPath ?? string.Empty;
            CursorThinkingCheckBox.IsChecked = preferences.Model.Contains("-thinking", StringComparison.OrdinalIgnoreCase);
            CursorAgentText.Text = string.IsNullOrWhiteSpace(preferences.AgentPath)
                ? "Cursor CLI: PATH에서 자동 검색"
                : $"Cursor CLI: {preferences.AgentPath}";
            UpdateCursorRuntimePaths(preferences);
            CursorApiKeyStatusText.Text = _cursorApiKeyStore.HasKey
                ? "Cursor User API Key가 Windows DPAPI에 저장되어 있습니다."
                : "Cursor User API Key가 저장되지 않았습니다. SSH 원격 Cursor 설치에는 이 키가 필요합니다.";

            if (autoStart && IsCursorProviderActive())
            {
                var status = await _cursorBridgeService.StartAsync(preferences);
                RenderCursorStatus(status);
            }
            else
            {
                RenderCursorStatus(_cursorBridgeService.Status);
            }

            CursorDisableButton.IsEnabled = IsCursorProviderActive();
        }
        catch (Exception error)
        {
            CursorStatusText.Text = error.Message;
            CursorDisableButton.IsEnabled = false;
        }
    }

    private void RenderCursorStatus(CursorBridgeStatus status)
    {
        var detail = string.IsNullOrWhiteSpace(status.Detail) ? string.Empty : $" · {status.Detail}";
        CursorStatusText.Text = status.ProcessId is null
            ? $"{status.Title}{detail}"
            : $"{status.Title} (PID {status.ProcessId}){detail}";
        CursorStatusText.Foreground = new SolidColorBrush(
            status.IsHealthy ? Colors.LimeGreen : Colors.LightGray);
        CursorDisableButton.IsEnabled = IsCursorProviderActive();
    }

    private void UpdateCursorRuntimePaths(CursorBridgePreferences? preferences = null)
    {
        try
        {
            preferences ??= _cursorBridgeService.LoadPreferences();
            var node = _cursorBridgeService.ResolveNode() ?? "찾지 못함";
            var agent = _cursorBridgeService.ResolveAgent(preferences.AgentPath) ?? "찾지 못함";
            CursorRuntimePathsText.Text =
                $"브리지: http://127.0.0.1:{preferences.Port}/healthz\nNode.js: {node}\nCursor CLI: {agent}\nCodex 설정: {_codexConfigService.ConfigurationFile}";
        }
        catch (Exception error)
        {
            CursorRuntimePathsText.Text = $"Cursor 실행 경로 확인 실패: {error.Message}";
        }
    }

    private async Task<IReadOnlyList<string>> SyncCursorProviderToEnabledDevicesAsync()
    {
        var apiKey = _cursorApiKeyStore.Read()
            ?? throw new CodexSyncBarException("Cursor User API Key를 먼저 저장해 주세요.");
        var preferences = _cursorBridgeService.LoadPreferences();
        var catalog = await _cursorBridgeService.LoadModelCatalogAsync(
            preferences.AgentPath,
            CancellationToken.None);
        var failures = new List<string>();
        foreach (var device in _configuration.Devices.Where(item => item.Enabled))
        {
            try
            {
                await _sshDeviceService.ProvisionCursorAsync(
                    device,
                    preferences,
                    catalog,
                    apiKey);
            }
            catch (Exception error)
            {
                failures.Add($"{device.DisplayLabel}: {error.Message}");
            }
        }

        return failures;
    }

    private async Task<IReadOnlyList<string>> DeprovisionCursorFromConfiguredDevicesAsync()
    {
        var failures = new List<string>();
        foreach (var device in _configuration.Devices)
        {
            try
            {
                await _sshDeviceService.DeprovisionCursorAsync(device);
            }
            catch (Exception error)
            {
                failures.Add($"{device.DisplayLabel}: {error.Message}");
            }
        }

        return failures;
    }

    private async void CursorSaveApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = CursorApiKeyBox.Password;
        if (string.IsNullOrEmpty(apiKey))
        {
            SetBanner("Cursor User API Key를 입력해 주세요.", isError: true);
            return;
        }

        SetBusy(true);
        try
        {
            using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            _cursorApiKeyStore.Save(apiKey);
            CursorApiKeyBox.Password = string.Empty;
            CursorApiKeyStatusText.Text = "Cursor User API Key가 Windows DPAPI에 저장되었습니다.";
            if (IsCursorProviderActive()
                && _configuration.Devices.Any(device => device.Enabled))
            {
                var failures = await SyncCursorProviderToEnabledDevicesAsync();
                SetBanner(
                    failures.Count == 0
                        ? "Cursor API key를 저장하고 활성 SSH 장치에 동기화했습니다."
                        : $"API key는 저장했지만 일부 SSH 장치 동기화에 실패했습니다: {string.Join(" · ", failures)}",
                    isError: failures.Count > 0);
            }
            else
            {
                SetBanner("Cursor API key를 저장했습니다. provider를 켜면 SSH 장치에도 동기화됩니다.", isError: false);
            }
        }
        catch (Exception error)
        {
            SetBanner($"Cursor API key 저장 실패: {error.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void CursorDeleteApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            _cursorApiKeyStore.Delete();
            CursorApiKeyBox.Password = string.Empty;
            CursorApiKeyStatusText.Text = "이 Windows PC의 Cursor API key를 삭제했습니다. 원격 저장본은 별도 제거가 필요합니다.";
            SetBanner("이 Windows PC의 Cursor API key를 삭제했습니다.", isError: false);
        }
        catch (Exception error)
        {
            SetBanner($"Cursor API key 삭제 실패: {error.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void CursorSyncRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsCursorProviderActive())
        {
            SetBanner("먼저 로컬 Cursor provider를 켜 주세요.", isError: true);
            return;
        }

        SetBusy(true);
        try
        {
            using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            var failures = await SyncCursorProviderToEnabledDevicesAsync();
            SetBanner(
                failures.Count == 0
                    ? "Cursor provider를 활성 SSH 장치에 동기화했습니다."
                    : $"일부 SSH 장치 동기화에 실패했습니다: {string.Join(" · ", failures)}",
                isError: failures.Count > 0);
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RenderUsage(UsageSnapshot snapshot)
    {
        FiveHourRow.Visibility = _usageDisplayPreferences.IsVisible(UsageDisplayItem.FiveHour)
            ? Visibility.Visible
            : Visibility.Collapsed;
        CodexWeeklyRow.Visibility = _usageDisplayPreferences.IsVisible(UsageDisplayItem.CodexWeekly)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SparkFiveHourRow.Visibility = _usageDisplayPreferences.IsVisible(UsageDisplayItem.SparkFiveHour)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SparkWeeklyRow.Visibility = _usageDisplayPreferences.IsVisible(UsageDisplayItem.SparkWeekly)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RenderQuota(snapshot.Session, FiveHourBar, FiveHourValue, FiveHourResetText);
        RenderQuota(snapshot.Weekly, WeeklyBar, WeeklyValue, WeeklyResetText);
        RenderQuota(snapshot.SparkSession, SparkFiveHourBar, SparkFiveHourValue, SparkFiveHourResetText);
        RenderQuota(snapshot.SparkWeekly, SparkWeeklyBar, SparkWeeklyValue, SparkWeeklyResetText);

        UsageStatusText.Text = "사용량을 확인했습니다.";
        UsageUpdatedText.Text = $"갱신 {snapshot.UpdatedAt.ToLocalTime():HH:mm:ss}";
        CreditsText.Text = snapshot.UnlimitedCredits
            ? "추가 크레딧: 무제한"
            : snapshot.CreditBalance.HasValue
                ? $"추가 크레딧: {snapshot.CreditBalance.Value:0.##}"
                : "추가 크레딧: 확인되지 않음";
        if (snapshot.ResetCredits.HasValue)
        {
            CreditsText.Text += $" · 초기화권 {snapshot.ResetCredits.Value}개";
        }
        ResetCreditsExpiryText.Text = UsageFormatting.CompactResetCreditExpiryDescription(
            snapshot.ResetCreditExpirations,
            DateTimeOffset.UtcNow)
            ?? (snapshot.ResetCredits.HasValue ? "만료 정보 없음" : string.Empty);
        UpdateTrayTitle();
    }

    private static void RenderQuota(
        UsageWindow? window,
        ProgressBar bar,
        TextBlock value,
        TextBlock reset)
    {
        bar.Value = window is null ? 0 : Math.Clamp(window.UsedPercent, 0, 100);
        value.Text = window is null
            ? "—"
            : $"{Math.Round(window.RemainingPercent):0}% 남음";
        reset.Text = window is null
            ? string.Empty
            : UsageFormatting.ResetDescription(window.ResetsAt, DateTimeOffset.UtcNow);
        bar.Opacity = window is null ? 0.25 : 1;
    }

    private async Task EvaluateWeeklyAnchorAsync(UsageSnapshot snapshot)
    {
        if (!_weeklyAnchorState.Preferences.IsEnabled(snapshot.ProfileId)
            || snapshot.Weekly is null
            || !_weeklyAnchorRunning.Add(snapshot.ProfileId))
        {
            return;
        }

        try
        {
            var record = _weeklyAnchorState.Records.GetValueOrDefault(snapshot.ProfileId)
                ?? new WeeklyAnchorRecord();
            var now = DateTimeOffset.UtcNow;
            var decision = WeeklyAnchorDecisionEngine.Decide(
                true,
                snapshot.Weekly,
                record,
                now);
            if (decision == WeeklyAnchorDecision.None)
            {
                _weeklyAnchorState.Records[snapshot.ProfileId] = record;
                _weeklyAnchorStore.Save(_weeklyAnchorState);
                return;
            }

            if (decision == WeeklyAnchorDecision.Observe)
            {
                record.NextResetAt = snapshot.Weekly.ResetsAt;
                record.ResetDriftCandidateAt = null;
                record.ResetDriftObservationCount = 0;
                _weeklyAnchorState.Records[snapshot.ProfileId] = record;
                _weeklyAnchorStore.Save(_weeklyAnchorState);
                return;
            }

            if (decision == WeeklyAnchorDecision.ConfirmResetDrift)
            {
                var observed = snapshot.Weekly.ResetsAt;
                if (observed is not null
                    && record.ResetDriftCandidateAt is { } candidate
                    && Math.Abs((candidate - observed.Value).TotalSeconds)
                        <= WeeklyAnchorDecisionEngine.ResetDriftTolerance.TotalSeconds)
                {
                    record.ResetDriftObservationCount++;
                }
                else
                {
                    record.ResetDriftCandidateAt = observed;
                    record.ResetDriftObservationCount = 1;
                }

                _weeklyAnchorState.Records[snapshot.ProfileId] = record;
                _weeklyAnchorStore.Save(_weeklyAnchorState);
                return;
            }

            if (decision == WeeklyAnchorDecision.AlreadyActive)
            {
                record.NextResetAt = snapshot.Weekly.ResetsAt;
                record.ResetDriftCandidateAt = null;
                record.ResetDriftObservationCount = 0;
                _weeklyAnchorState.Records[snapshot.ProfileId] = record;
                _weeklyAnchorStore.Save(_weeklyAnchorState);
                return;
            }

            var expectedResetAt = record.NextResetAt;
            record.LastAttemptAt = now;
            record.LastError = null;
            _weeklyAnchorState.Records[snapshot.ProfileId] = record;
            _weeklyAnchorStore.Save(_weeklyAnchorState);
            var response = await _weeklyAnchorService.SendAsync(snapshot.ProfileId);
            var completedAt = DateTimeOffset.UtcNow;
            var observedNextResetAt = snapshot.Weekly?.ResetsAt;
            record.LastSuccessAt = completedAt;
            record.LastHandledResetAt = expectedResetAt;
            record.NextResetAt = observedNextResetAt is { } next && next > completedAt
                ? next
                : null;
            record.ResetDriftCandidateAt = null;
            record.ResetDriftObservationCount = 0;
            record.LastError = null;
            _weeklyAnchorState.Records[snapshot.ProfileId] = record;
            _weeklyAnchorStore.Save(_weeklyAnchorState);
            SetBanner(
                string.IsNullOrWhiteSpace(response)
                    ? "주간 anchor 메시지를 보냈습니다."
                : $"주간 anchor 완료: {response}",
                isError: false);
            await LoadUsageAsync(snapshot.ProfileId);
        }
        catch (Exception error)
        {
            var record = _weeklyAnchorState.Records.GetValueOrDefault(snapshot.ProfileId)
                ?? new WeeklyAnchorRecord();
            record.LastError = error.Message;
            _weeklyAnchorState.Records[snapshot.ProfileId] = record;
            _weeklyAnchorStore.Save(_weeklyAnchorState);
            MarkAccountNeedsLoginIfCanonicalFailure(snapshot.ProfileId, error);
            SetBanner($"주간 anchor에 실패했습니다: {error.Message}", isError: true);
        }
        finally
        {
            _weeklyAnchorRunning.Remove(snapshot.ProfileId);
        }
    }

    private void SetWeeklyAnchorEnabled(int profileId, bool enabled)
    {
        _weeklyAnchorState.Preferences.SetEnabled(profileId, enabled);
        _weeklyAnchorStore.Save(_weeklyAnchorState);
    }

    private string WeeklyAnchorStatus(int profileId)
    {
        if (!_weeklyAnchorState.Preferences.IsEnabled(profileId))
        {
            return "사용 안 함";
        }

        var record = _weeklyAnchorState.Records.GetValueOrDefault(profileId);
        if (!string.IsNullOrWhiteSpace(record?.LastError))
        {
            if (record.LastAttemptAt is { } lastAttempt)
            {
                var retryAt = lastAttempt + WeeklyAnchorDecisionEngine.RetryInterval;
                if (retryAt > DateTimeOffset.UtcNow)
                {
                    return $"실행 실패 · {UsageFormatting.ResetCreditExpiryDescription(retryAt)} 후 재시도";
                }
            }

            return "실행 실패 · 다음 확인 때 재시도";
        }

        if (_weeklyAnchorRunning.Contains(profileId))
        {
            return "메시지 전송 중…";
        }

        if (record?.ResetDriftObservationCount > 0)
        {
            return "초기화 시각 변경 확인 중…";
        }

        if (record?.NextResetAt is { } nextReset && nextReset > DateTimeOffset.UtcNow)
        {
            return $"{UsageFormatting.ResetCreditExpiryDescription(nextReset)} 후 자동 실행";
        }

        return record?.LastSuccessAt is { } success
            ? $"최근 실행 {success.ToLocalTime():MM-dd HH:mm}"
            : "주간 사용량 확인 대기";
    }

    private async Task StartWeeklyAnchorNowAsync(int profileId)
    {
        if (!_weeklyAnchorRunning.Add(profileId))
        {
            return;
        }

        try
        {
            var record = _weeklyAnchorState.Records.GetValueOrDefault(profileId)
                ?? new WeeklyAnchorRecord();
            var expectedResetAt = record.NextResetAt;
            record.LastAttemptAt = DateTimeOffset.UtcNow;
            record.LastError = null;
            _weeklyAnchorState.Records[profileId] = record;
            _weeklyAnchorStore.Save(_weeklyAnchorState);
            var response = await _weeklyAnchorService.SendAsync(profileId);
            var completedAt = DateTimeOffset.UtcNow;
            var observedNextResetAt = _usageSnapshots.GetValueOrDefault(profileId)?.Weekly?.ResetsAt;
            record.LastSuccessAt = completedAt;
            record.LastHandledResetAt = expectedResetAt;
            record.NextResetAt = observedNextResetAt is { } next && next > completedAt
                ? next
                : null;
            record.ResetDriftCandidateAt = null;
            record.ResetDriftObservationCount = 0;
            _weeklyAnchorState.Records[profileId] = record;
            _weeklyAnchorStore.Save(_weeklyAnchorState);
            SetBanner(
                string.IsNullOrWhiteSpace(response)
                    ? "주간 anchor 메시지를 보냈습니다."
                    : $"주간 anchor 완료: {response}",
                isError: false);
            await LoadUsageAsync(profileId);
        }
        catch (Exception error)
        {
            var record = _weeklyAnchorState.Records.GetValueOrDefault(profileId)
                ?? new WeeklyAnchorRecord();
            record.LastError = error.Message;
            _weeklyAnchorState.Records[profileId] = record;
            _weeklyAnchorStore.Save(_weeklyAnchorState);
            MarkAccountNeedsLoginIfCanonicalFailure(profileId, error);
            SetBanner($"주간 anchor에 실패했습니다: {error.Message}", isError: true);
        }
        finally
        {
            _weeklyAnchorRunning.Remove(profileId);
        }
    }

    private void ResetUsageView()
    {
        foreach (var bar in new[] { FiveHourBar, WeeklyBar, SparkFiveHourBar, SparkWeeklyBar })
        {
            bar.Value = 0;
            bar.Opacity = 0.25;
        }
        foreach (var text in new[] { FiveHourValue, WeeklyValue, SparkFiveHourValue, SparkWeeklyValue })
        {
            text.Text = "—";
        }
        foreach (var text in new[] { FiveHourResetText, WeeklyResetText, SparkFiveHourResetText, SparkWeeklyResetText })
        {
            text.Text = string.Empty;
        }
        CreditsText.Text = string.Empty;
        ResetCreditsExpiryText.Text = string.Empty;
        UsageUpdatedText.Text = string.Empty;
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            var activeProfileId = _localSwitchService.GetActiveProfileId(_configuration.Accounts);
            var statuses = await _sshDeviceService.FetchStatusesAsync(_configuration, activeProfileId);
            _deviceRows.Clear();
            foreach (var status in statuses)
            {
                var configured = _configuration.Devices.FirstOrDefault(item =>
                    string.Equals(item.Id, status.Id, StringComparison.OrdinalIgnoreCase));
                _deviceRows.Add(new DeviceRow(status, configured));
            }

            var selectedRow = _deviceRows.FirstOrDefault(row =>
                string.Equals(row.Id, _selectedDeviceId, StringComparison.OrdinalIgnoreCase));
            DevicesList.SelectedItem = selectedRow;
            UpdateDeviceActions();
            UpdateTrayTitle();
        }
        catch (Exception error)
        {
            SetBanner($"장치 상태를 확인하지 못했습니다: {error.Message}", isError: true);
        }
    }

    private void DevicesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedDeviceId = (DevicesList.SelectedItem as DeviceRow)?.Id;
        UpdateDeviceActions();
    }

    private void UpdateDeviceActions()
    {
        var selected = _configuration.Devices.FirstOrDefault(device =>
            string.Equals(device.Id, _selectedDeviceId, StringComparison.OrdinalIgnoreCase));
        var canEdit = !_isBusy && !_configurationRecoveryNeeded && selected is not null;
        EditDeviceButton.IsEnabled = canEdit;
        RemoveDeviceButton.IsEnabled = canEdit;
        ActivateDeviceButton.IsEnabled = canEdit && selected!.Enabled is false;
        TestDeviceButton.IsEnabled = canEdit && selected!.Enabled;
    }

    private async Task SaveDeviceAsync(DeviceDialog dialog)
    {
        using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
        var draft = dialog.Device
            ?? throw new CodexSyncBarException("SSH 장치 설정이 비어 있습니다.");
        var existing = _configuration.Devices.FirstOrDefault(device =>
            string.Equals(device.Id, draft.Id, StringComparison.OrdinalIgnoreCase));
        var prepared = _sshDeviceService.PrepareForSave(
            _configuration,
            draft,
            dialog.Password,
            dialog.Passphrase,
            dialog.ClearPassword,
            dialog.ClearPassphrase);
        var committed = false;
        try
        {
            _configurationStore.UpsertDevice(_configuration, prepared.Device);
            committed = true;
            foreach (var intent in prepared.SecretCleanupIntents
                .Where(item => item.CredentialId == prepared.Device.CredentialId))
            {
                _sshDeviceService.CompleteSecretCleanup(intent.Path);
            }

            foreach (var intent in prepared.SecretCleanupIntents
                .Where(item => item.CredentialId != prepared.Device.CredentialId))
            {
                _sshDeviceService.DeleteSecrets(intent.CredentialId);
                _sshDeviceService.CompleteSecretCleanup(intent.Path);
            }

            _selectedDeviceId = prepared.Device.Id;
            await RefreshDevicesAsync();
            SetBanner(
                prepared.RequiresActivationValidation
                    ? "SSH 장치를 저장했습니다. ‘설치 및 활성화’로 연결과 원격 Codex 설치를 검증해 주세요."
                    : "SSH 장치 설정을 저장했습니다.",
                isError: false);
        }
        catch
        {
            if (!committed && prepared.Device.CredentialId is { } credentialId)
            {
                foreach (var intent in prepared.SecretCleanupIntents)
                {
                    if (intent.CredentialId == credentialId)
                    {
                        try
                        {
                            _sshDeviceService.DeleteSecrets(credentialId);
                            _sshDeviceService.CompleteSecretCleanup(intent.Path);
                        }
                        catch
                        {
                            // The durable intent remains for the next launch.
                        }
                    }
                    else
                    {
                        _sshDeviceService.CompleteSecretCleanup(intent.Path);
                    }
                }
            }

            throw;
        }
    }

    private async void EditDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        var existing = _configuration.Devices.FirstOrDefault(device =>
            string.Equals(device.Id, _selectedDeviceId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            SetBanner("편집할 SSH 장치를 선택해 주세요.", isError: true);
            return;
        }

        var dialog = new DeviceDialog(existing)
        {
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await SaveDeviceAsync(dialog);
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
        }
    }

    private async void ActivateDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        var device = _configuration.Devices.FirstOrDefault(item =>
            string.Equals(item.Id, _selectedDeviceId, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            SetBanner("설치할 SSH 장치를 선택해 주세요.", isError: true);
            return;
        }

        SetBusy(true);
        string? activationIntentPath = null;
        try
        {
            using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            var test = await _sshDeviceService.TestConnectionAsync(device);
            if (!test.IsReachable)
            {
                throw new CodexSyncBarException(test.Message);
            }

            var bootstrap = await _sshDeviceService.BootstrapAsync(
                _configuration,
                device,
                _localSwitchService.GetActiveProfileId(_configuration.Accounts));
            activationIntentPath = _deviceActivationTransactions.Save(device);
            _configurationStore.BeginDeviceActivation(_configuration, device);
            var statuses = await _sshDeviceService.FetchStatusesAsync(
                _configuration,
                _localSwitchService.GetActiveProfileId(_configuration.Accounts));
            var verified = statuses.FirstOrDefault(item =>
                string.Equals(item.Id, device.Id, StringComparison.OrdinalIgnoreCase));
            if (verified is null
                || verified.ProfileId != bootstrap.ActiveProfileId)
            {
                throw new CodexSyncBarException("활성화 후 SSH 장치의 원격 계정 상태를 확인하지 못했습니다.");
            }

            _deviceActivationTransactions.Delete(activationIntentPath);
            activationIntentPath = null;
            await RefreshDevicesAsync();
            if (IsCursorProviderActive() && _cursorApiKeyStore.HasKey)
            {
                try
                {
                    var preferences = _cursorBridgeService.LoadPreferences();
                    var catalog = await _cursorBridgeService.LoadModelCatalogAsync(preferences.AgentPath);
                    await _sshDeviceService.ProvisionCursorAsync(
                        device,
                        preferences,
                        catalog,
                        _cursorApiKeyStore.Read()!);
                    SetBanner($"{device.DisplayLabel} 설치, 계정 동기화, Cursor provider 활성화를 완료했습니다.", isError: false);
                }
                catch (Exception cursorError)
                {
                    SetBanner(
                        $"{device.DisplayLabel}의 Codex 계정 동기화는 완료됐지만 Cursor 설치는 확인이 필요합니다: {cursorError.Message}",
                        isError: true);
                }
            }
            else
            {
                SetBanner($"{device.DisplayLabel} 설치와 활성화를 완료했습니다.", isError: false);
            }
        }
        catch (Exception error)
        {
            if (activationIntentPath is not null)
            {
                try
                {
                    _configurationStore.RollbackDeviceActivation(_configuration, device);
                    _deviceActivationTransactions.Delete(activationIntentPath);
                    activationIntentPath = null;
                }
                catch (Exception recoveryError)
                {
                    SetBanner(
                        $"{device.DisplayLabel} 활성화 복구가 필요합니다. 앱을 다시 열어 복구해 주세요: {recoveryError.Message}",
                        isError: true);
                    return;
                }
            }

            SetBanner($"{device.DisplayLabel} 활성화에 실패했습니다: {error.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void TestDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        var device = _configuration.Devices.FirstOrDefault(item =>
            string.Equals(item.Id, _selectedDeviceId, StringComparison.OrdinalIgnoreCase));
        if (device is null || !device.Enabled)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _sshDeviceService.TestAsync(device);
            SetBanner(result.Message, isError: !result.IsReachable);
            await RefreshDevicesAsync();
        }
        catch (Exception error)
        {
            SetBanner($"SSH helper 테스트 실패: {error.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RemoveDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        var device = _configuration.Devices.FirstOrDefault(item =>
            string.Equals(item.Id, _selectedDeviceId, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "SSH 장치 제거",
            Content = $"{device.DisplayLabel} 장치를 제거하고 저장된 SSH 비밀도 삭제할까요?",
            PrimaryButtonText = "제거",
            SecondaryButtonText = "취소",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string? cleanupIntent = null;
        var committed = false;
        try
        {
            using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            if (device.CredentialId is { } credentialId)
            {
                cleanupIntent = _sshDeviceService.BeginSecretCleanup(credentialId);
            }

            _configurationStore.RemoveDevice(_configuration, device.Id);
            committed = true;
            if (device.CredentialId is { } removedCredentialId)
            {
                _sshDeviceService.DeleteSecrets(removedCredentialId);
                if (cleanupIntent is not null)
                {
                    _sshDeviceService.CompleteSecretCleanup(cleanupIntent);
                    cleanupIntent = null;
                }
            }

            _selectedDeviceId = null;
            await RefreshDevicesAsync();
            SetBanner("SSH 장치와 저장된 자격 증명을 제거했습니다.", isError: false);
        }
        catch (Exception error)
        {
            if (!committed && cleanupIntent is not null)
            {
                try
                {
                    _sshDeviceService.CompleteSecretCleanup(cleanupIntent);
                }
                catch
                {
                    // Keep the transaction if cancellation of the intent is
                    // uncertain; startup recovery will compare the device
                    // configuration before deleting anything.
                }
            }

            SetBanner(error.Message, isError: true);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void RefreshSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var account = GetSelectedAccount();
        if (account is null || account.IsPending || account.NeedsLogin)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await LoadUsageAsync(account.Id);
            SetBanner("선택한 계정의 사용량을 갱신했습니다.", isError: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            await RefreshDevicesAsync();
            SetBanner("장치 상태를 갱신했습니다.", isError: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void AddAccountButton_Click(object sender, RoutedEventArgs e)
    {
        ControllerMutationLock? mutationLock = null;
        try
        {
            mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            var account = _configurationStore.ReserveAccount(_configuration);
            UpdateAccountsView(account.Id);
            await RunLoginCoreAsync(account, replaceExisting: false);
            if (account.IsPending
                && !_authStore.ProfileArtifactExists(account.Id)
                && _configuration.Accounts.Count > 1)
            {
                _configurationStore.RemoveAccount(_configuration, account.Id);
                RemoveAccountState(account.Id);
                UpdateAccountsView(_configuration.Accounts.FirstOrDefault()?.Id);
            }
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
        }
        finally
        {
            mutationLock?.Dispose();
        }
    }

    private async void MoveAccountButton_Click(object sender, RoutedEventArgs e)
    {
        var account = GetSelectedAccount();
        var direction = (sender as Button)?.Tag?.ToString();
        if (account is null || direction is not ("up" or "down"))
        {
            return;
        }

        var index = _configuration.Accounts.FindIndex(item => item.Id == account.Id);
        var destination = direction == "up" ? index - 1 : index + 1;
        if (index < 0 || destination < 0 || destination >= _configuration.Accounts.Count)
        {
            return;
        }

        try
        {
            using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            var orderedIds = _configuration.Accounts.Select(item => item.Id).ToList();
            (orderedIds[index], orderedIds[destination]) = (orderedIds[destination], orderedIds[index]);
            _configurationStore.ReorderAccounts(_configuration, orderedIds);
            UpdateAccountsView(account.Id);
            SetBanner("계정 순서를 저장했습니다.", isError: false);
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
        }
    }

    private async void ImportAuthButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var account = GetSelectedAccount();
            if (account is null)
            {
                SetBanner("먼저 계정을 선택해 주세요.", isError: true);
                return;
            }

            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".json");
            var window = ((App)Application.Current).MainWindow
                ?? throw new CodexSyncBarException("앱 창을 찾지 못했습니다.");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(window));
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            _loginTransactions.ImportAuth(
                _authStore,
                file.Path,
                account.Id,
                replaceExisting: !account.IsPending);
            var credentials = _authStore.ReadCredentials(account.Id);
            _configurationStore.UpdateAccountEmail(_configuration, account.Id, credentials.Email);
            UpdateAccountsView(account.Id);
            await LoadUsageAsync(account.Id);
            var syncFailures = await _sshDeviceService.SyncProfileAsync(_configuration, account.Id);
            SetBanner(
                syncFailures.Count == 0
                    ? "인증 정보를 안전하게 등록하고 SSH 장치에 동기화했습니다."
                    : $"인증은 등록했지만 일부 SSH 장치 동기화가 보류되었습니다: {string.Join(" · ", syncFailures)}",
                isError: syncFailures.Count > 0);
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var account = GetSelectedAccount();
        if (account is null)
        {
            SetBanner("먼저 계정을 선택해 주세요.", isError: true);
            return;
        }

        await RunLoginAsync(account, replaceExisting: !account.IsPending);
    }

    private void CancelLoginButton_Click(object sender, RoutedEventArgs e)
    {
        _loginCancellation?.Cancel();
        if (_lastLoginProfileId is { } profileId)
        {
            _browserLoginService.CloseLoginWindow(profileId);
        }
    }

    private async void RetryLoginButton_Click(object sender, RoutedEventArgs e)
    {
        var account = GetSelectedAccount();
        if (account is null || _isBusy)
        {
            return;
        }

        await RunLoginAsync(account, _lastLoginReplaceExisting);
    }

    private void ReopenLoginButton_Click(object sender, RoutedEventArgs e)
    {
        var account = GetSelectedAccount();
        if (account is null || _isBusy)
        {
            return;
        }

        try
        {
            _browserLoginService.ReopenLogin(account.Id);
            SetBanner("Windows 기본 브라우저에서 로그인 페이지를 다시 열었습니다.", isError: false);
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
        }
    }

    private async void FreshLoginButton_Click(object sender, RoutedEventArgs e)
    {
        var account = GetSelectedAccount();
        if (account is null || _isBusy)
        {
            return;
        }

        SetBanner("Windows 기본 브라우저에서 다른 계정 로그인을 엽니다…", isError: false);
        await RunLoginAsync(account, replaceExisting: true);
    }

    private async Task RunLoginAsync(AccountProfile account, bool replaceExisting)
    {
        using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
        await RunLoginCoreAsync(account, replaceExisting);
    }

    private async Task RunLoginCoreAsync(AccountProfile account, bool replaceExisting)
    {
        var loginCancellation = new CancellationTokenSource();
        _loginCancellation = loginCancellation;
        _lastLoginProfileId = account.Id;
        _lastLoginReplaceExisting = replaceExisting;
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(message => SetBanner(message, isError: false));
            await _codexLoginService.LoginAsync(
                account.Id,
                replaceExisting,
                progress,
                loginCancellation.Token);
            var credentials = _authStore.ReadCredentials(account.Id);
            _configurationStore.UpdateAccountEmail(_configuration, account.Id, credentials.Email);
            UpdateAccountsView(account.Id);
            await LoadUsageAsync(account.Id);
            var syncFailures = await _sshDeviceService.SyncProfileAsync(_configuration, account.Id);
            SetBanner(
                syncFailures.Count == 0
                    ? "로그인과 인증 저장, SSH 장치 동기화가 완료되었습니다."
                    : $"로그인은 완료했지만 일부 SSH 장치 동기화가 보류되었습니다: {string.Join(" · ", syncFailures)}",
                isError: syncFailures.Count > 0);
            _lastLoginProfileId = null;
        }
        catch (OperationCanceledException)
        {
            SetBanner("로그인을 취소했습니다.", isError: false);
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
        }
        finally
        {
            if (ReferenceEquals(_loginCancellation, loginCancellation))
            {
                _loginCancellation = null;
            }

            loginCancellation.Dispose();
            SetBusy(false);
            UpdateLoginActions(GetSelectedAccount());
        }
    }

    private async void EditAliasButton_Click(object sender, RoutedEventArgs e)
    {
        var account = GetSelectedAccount();
        if (account is null)
        {
            return;
        }

        var editor = new TextBox
        {
            Text = account.CustomAlias ?? string.Empty,
            PlaceholderText = "비워 두면 이메일을 표시합니다.",
            MaxLength = AccountProfile.MaximumAliasLength,
        };
        var dialog = new ContentDialog
        {
            Title = "계정 별칭",
            Content = editor,
            PrimaryButtonText = "저장",
            SecondaryButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            _configurationStore.UpdateAccountAlias(_configuration, account.Id, editor.Text);
            UpdateAccountsView(account.Id);
            SetBanner("별칭을 저장했습니다.", isError: false);
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
        }
    }

    private async void DeleteAccountButton_Click(object sender, RoutedEventArgs e)
    {
        var account = GetSelectedAccount();
        if (account is null || _configuration.Accounts.Count <= 1)
        {
            return;
        }

        if (_authStore.ProfileArtifactExists(account.Id))
        {
            SetBanner("계정 항목을 제거하기 전에 먼저 로그아웃해 주세요.", isError: true);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "계정 제거",
            Content = $"{account.Alias} 계정 항목을 제거할까요? 기본 브라우저의 로그인 세션은 유지됩니다.",
            PrimaryButtonText = "제거",
            SecondaryButtonText = "취소",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        try
        {
            using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            string? browserWarning = null;
            try
            {
                _browserLoginService.ClearProfile(account.Id);
            }
            catch (Exception browserError)
            {
                _browserLoginService.MarkCleanupPending(account.Id);
                browserWarning = browserError.Message;
            }

            _configurationStore.RemoveAccount(_configuration, account.Id);
            RemoveAccountState(account.Id);
            UpdateAccountsView(_configuration.Accounts.FirstOrDefault()?.Id);
            await LoadUsageAsync(_selectedProfileId ?? 0);
            SetBanner(
                browserWarning is null
                    ? "계정 항목을 제거했습니다."
                    : $"계정 항목은 제거했지만 이전 격리 브라우저 세션 정리가 필요합니다: {browserWarning}",
                isError: browserWarning is not null);
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void LogoutAccountButton_Click(object sender, RoutedEventArgs e)
    {
        var account = GetSelectedAccount();
        if (account is null
            || _configuration.Accounts.Count <= 1
            || account.IsPending
            || account.NeedsLogin
            || !_authStore.ProfileArtifactExists(account.Id))
        {
            return;
        }

        var fallback = _configuration.Accounts.FirstOrDefault(candidate =>
            candidate.Id != account.Id
            && !candidate.IsPending
            && !candidate.NeedsLogin
            && _authStore.ProfileArtifactExists(candidate.Id));
        if (fallback is null)
        {
            SetBanner("로그아웃하려면 다른 로그인된 계정을 fallback으로 준비해 주세요.", isError: true);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "계정 로그아웃",
            Content = $"{account.Alias} 계정을 이 PC와 모든 활성 SSH 장치에서 로그아웃할까요? 계정 항목은 유지되어 나중에 재로그인할 수 있습니다.",
            PrimaryButtonText = "로그아웃",
            SecondaryButtonText = "취소",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        try
        {
            using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            await _sshDeviceService.LogoutAsync(_configuration, account.Id, fallback.Id);
            _configurationStore.MarkAccountLoggedOut(_configuration, account.Id);

            string? browserWarning = null;
            try
            {
                _browserLoginService.ClearProfile(account.Id);
            }
            catch (Exception browserError)
            {
                _browserLoginService.MarkCleanupPending(account.Id);
                browserWarning = browserError.Message;
            }

            _usageSnapshots.Remove(account.Id);
            _usageErrors[account.Id] = "로그아웃되었습니다.";
            UpdateAccountsView(account.Id);
            await LoadUsageAsync(account.Id);
            SetBanner(
                browserWarning is null
                    ? $"{account.Alias} 계정을 로그아웃했습니다. 계정 항목은 유지됩니다."
                    : $"로그아웃했지만 이전 격리 브라우저 세션 정리가 필요합니다: {browserWarning}",
                isError: browserWarning is not null);
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        var account = GetSelectedAccount();
        if (account is null || account.IsPending || account.NeedsLogin)
        {
            SetBanner("로그인이 완료된 계정을 선택해 주세요.", isError: true);
            return;
        }

        await ApplyAccountAsync(account);
    }

    private async Task ApplyAccountAsync(AccountProfile account)
    {
        if (account.IsPending || account.NeedsLogin || !_authStore.ProfileArtifactExists(account.Id))
        {
            SetBanner("로그인이 완료된 계정을 선택해 주세요.", isError: true);
            return;
        }

        SetBusy(true);
        try
        {
            using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            var remoteCount = _configuration.Devices.Count(device => device.Enabled);
            await _sshDeviceService.SwitchAllAsync(_configuration, account.Id);
            await RefreshDevicesAsync();
            UpdateSelectedAccount(account);
            await LoadUsageAsync(account.Id);
            SetBanner(remoteCount == 0
                ? "이 Windows PC에 계정을 적용했습니다."
                : $"이 Windows PC와 SSH 장치 {remoteCount}대에 계정을 적용했습니다.", isError: false);
        }
        catch (Exception error)
        {
            SetBanner($"계정 전환에 실패했습니다. 변경된 장치의 복구 결과를 확인해 주세요: {error.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void AddDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DeviceDialog
        {
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Device is null)
        {
            return;
        }

        try
        {
            await SaveDeviceAsync(dialog);
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
        }
    }

    private async void RefreshTokenUsageButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            await RefreshTokenUsageAsync();
            SetBanner("토큰 사용량을 갱신했습니다.", isError: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void CursorRefreshModelsButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var configuredAgentPath = string.IsNullOrWhiteSpace(CursorAgentPathTextBox.Text)
                ? null
                : CursorAgentPathTextBox.Text.Trim();
            var preferences = _cursorBridgeService.LoadPreferences();
            var catalog = await _cursorBridgeService.LoadModelCatalogAsync(configuredAgentPath ?? preferences.AgentPath);
            _cursorModelCatalog = catalog;
            UpdateCursorRuntimePaths(preferences);
            CursorModelsText.Text = string.Join(", ", catalog.Variants.Select(item => item.Slug));
            var currentModel = CursorModelDraft();
            SetCursorModelDraft(catalog.Variants.Any(item => item.Slug == currentModel)
                ? currentModel
                : catalog.SuggestedModel);
            UpdateCursorThinkingControl();

            SetBanner($"Cursor 모델 {catalog.Variants.Count}개를 확인했습니다.", isError: false);
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
            CursorStatusText.Text = error.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CursorModelTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateCursorThinkingControl();

    private void CursorModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCursorModelPicker
            || CursorModelPicker.SelectedItem is not ComboBoxItem { Tag: string model })
        {
            return;
        }

        CursorModelTextBox.Text = model;
        UpdateCursorThinkingControl();
    }

    private string CursorModelDraft()
    {
        if (CursorModelPicker.Visibility == Visibility.Visible
            && CursorModelPicker.SelectedItem is ComboBoxItem { Tag: string model })
        {
            return model;
        }

        return CursorModelTextBox.Text.Trim();
    }

    private void SetCursorModelDraft(string model)
    {
        model = string.IsNullOrWhiteSpace(model) ? "auto" : model.Trim();
        _updatingCursorModelPicker = true;
        try
        {
            if (_cursorModelCatalog is { Variants.Count: > 0 })
            {
                CursorModelPicker.Items.Clear();
                foreach (var variant in _cursorModelCatalog.Variants)
                {
                    CursorModelPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{variant.DisplayName} · {variant.Slug}",
                        Tag = variant.Slug,
                    });
                }

                var selectedIndex = _cursorModelCatalog.Variants
                    .Select(item => item.Slug)
                    .ToList()
                    .FindIndex(item => item == model);
                if (selectedIndex >= 0)
                {
                    CursorModelPicker.SelectedIndex = selectedIndex;
                    CursorModelPicker.Visibility = Visibility.Visible;
                    CursorModelTextBox.Visibility = Visibility.Collapsed;
                }
                else
                {
                    CursorModelPicker.SelectedIndex = -1;
                    CursorModelPicker.Visibility = Visibility.Collapsed;
                    CursorModelTextBox.Visibility = Visibility.Visible;
                }
            }
            else
            {
                CursorModelPicker.Visibility = Visibility.Collapsed;
                CursorModelTextBox.Visibility = Visibility.Visible;
            }

            CursorModelTextBox.Text = model;
        }
        finally
        {
            _updatingCursorModelPicker = false;
        }

        UpdateCursorThinkingControl();
    }

    private void UpdateCursorThinkingControl()
    {
        var model = CursorModelDraft();
        var selected = _cursorModelCatalog?.Variants.FirstOrDefault(item => item.Slug == model);
        CursorThinkingCheckBox.IsChecked = selected?.Thinking
            ?? model.Contains("-thinking", StringComparison.OrdinalIgnoreCase);
        CursorThinkingCheckBox.IsEnabled = selected is not null
            && _cursorModelCatalog!.Variants.Any(item =>
                item.BaseSlug == selected.BaseSlug && item.Thinking != selected.Thinking);
    }

    private void CursorThinkingCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var catalog = _cursorModelCatalog;
        var current = catalog?.Variants.FirstOrDefault(item => item.Slug == CursorModelDraft());
        if (catalog is null || current is null)
        {
            return;
        }

        var targetThinking = CursorThinkingCheckBox.IsChecked == true;
        var target = catalog.Variants.FirstOrDefault(item =>
                item.BaseSlug == current.BaseSlug
                && item.Thinking == targetThinking
                && item.Effort == current.Effort
                && item.Fast == current.Fast)
            ?? catalog.Variants.FirstOrDefault(item =>
                item.BaseSlug == current.BaseSlug && item.Thinking == targetThinking);
        if (target is null)
        {
            CursorThinkingCheckBox.IsChecked = current.Thinking;
            return;
        }

        SetCursorModelDraft(target.Slug);
    }

    private async void CursorGuideButton_Click(object sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://cursor.com/docs/cli/installation"));
    }

    private void CursorCopyLoginButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText("cursor-agent login");
        Clipboard.SetContent(package);
        SetBanner("cursor-agent login 명령을 클립보드에 복사했습니다.", isError: false);
    }

    private async void CursorEnableButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CursorPortTextBox.Text.Trim(), out var port))
        {
            SetBanner("Cursor 브리지 포트를 숫자로 입력해 주세요.", isError: true);
            return;
        }

        SetBusy(true);
        try
        {
            using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            var current = _cursorBridgeService.LoadPreferences();
            var preferences = current.Clone();
            preferences.Model = string.IsNullOrWhiteSpace(CursorModelDraft())
                ? "auto"
                : CursorModelDraft();
            preferences.Port = port;
            preferences.AgentPath = string.IsNullOrWhiteSpace(CursorAgentPathTextBox.Text)
                ? null
                : CursorAgentPathTextBox.Text.Trim();
            var status = await _cursorProviderService.EnableAsync(preferences);
            RenderCursorStatus(status);
            var savedPreferences = _cursorBridgeService.LoadPreferences();
            SetCursorModelDraft(savedPreferences.Model);
            CursorPortTextBox.Text = savedPreferences.Port.ToString();
            CursorAgentPathTextBox.Text = savedPreferences.AgentPath ?? string.Empty;
            UpdateCursorRuntimePaths(savedPreferences);
            UpdateCursorThinkingControl();
            if (_cursorApiKeyStore.HasKey && _configuration.Devices.Any(device => device.Enabled))
            {
                var failures = await SyncCursorProviderToEnabledDevicesAsync();
                SetBanner(
                    failures.Count == 0
                        ? "Cursor provider를 Codex와 활성 SSH 장치에 연결했습니다."
                        : $"로컬 Cursor provider는 연결했지만 일부 SSH 동기화에 실패했습니다: {string.Join(" · ", failures)}",
                    isError: failures.Count > 0);
            }
            else
            {
                SetBanner("Cursor provider를 Codex에 연결했습니다.", isError: false);
            }
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
            CursorStatusText.Text = error.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void CursorDisableButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            await _cursorProviderService.DisableAsync();
            var failures = await DeprovisionCursorFromConfiguredDevicesAsync();
            RenderCursorStatus(_cursorBridgeService.Status);
            SetBanner(
                failures.Count == 0
                    ? "Cursor provider를 끄고 Codex 설정과 SSH 원격 provider를 원복했습니다."
                    : $"로컬 Cursor provider는 원복했지만 일부 SSH 정리에 실패했습니다: {string.Join(" · ", failures)}",
                isError: failures.Count > 0);
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
            CursorStatusText.Text = error.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_configurationRecoveryNeeded)
        {
            SetBanner("설정 복구가 필요합니다. 먼저 새로고침을 다시 시도해 주세요.", isError: true);
            return;
        }

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "Windows 저장 위치",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Codex SyncBar {typeof(App).Assembly.GetName().Version?.ToString(3) ?? "development"}",
            Style = (Style)Resources["MutedText"],
        });
        content.Children.Add(new TextBlock
        {
            Text = $"설정·프로필: {_paths.StateRoot}\nCodex: {_paths.CodexHome}\n브라우저: Windows 기본 브라우저\n이전 격리 세션: {_paths.ChromeProfilesDirectory}\nCursor catalog: {_paths.CursorModelCatalogFile}",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = "macOS Keychain 대신 Windows DPAPI를 사용해 Cursor API key와 SSH 비밀번호·키 암호를 사용자 계정에 묶어 저장합니다. 원격 연결은 Windows OpenSSH를 사용합니다.",
            Style = (Style)Resources["MutedText"],
            TextWrapping = TextWrapping.Wrap,
        });
        var launchAtLogin = new CheckBox
        {
            Content = "Windows 로그인 시 Codex SyncBar 자동 시작",
            IsChecked = LaunchAtLoginService.IsEnabled,
        };
        launchAtLogin.Click += (_, _) =>
        {
            try
            {
                LaunchAtLoginService.SetEnabled(launchAtLogin.IsChecked == true);
            }
            catch (Exception error)
            {
                launchAtLogin.IsChecked = !launchAtLogin.IsChecked;
                SetBanner(error.Message, isError: true);
            }
        };
        content.Children.Add(launchAtLogin);
        var openStartupSettings = new Button
        {
            Content = "Windows 시작 앱 설정 열기",
            Style = (Style)Resources["ActionButton"],
        };
        openStartupSettings.Click += (_, _) =>
        {
            try
            {
                LaunchAtLoginService.OpenSettings();
            }
            catch (Exception error)
            {
                SetBanner(error.Message, isError: true);
            }
        };
        content.Children.Add(openStartupSettings);
        content.Children.Add(new TextBlock
        {
            Text = "사용량 표시",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        });
        foreach (var item in Enum.GetValues<UsageDisplayItem>())
        {
            var displayItem = item;
            var checkBox = new CheckBox
            {
                Content = displayItem.Title(),
                IsChecked = _usageDisplayPreferences.IsVisible(displayItem),
            };
            checkBox.Click += (_, _) =>
            {
                _usageDisplayPreferences.SetVisible(displayItem, checkBox.IsChecked == true);
                _usageDisplayStore.SaveUsagePreferences(_usageDisplayPreferences);
                if (_lastUsageSnapshot is not null)
                {
                    RenderUsage(_lastUsageSnapshot);
                }
            };
            content.Children.Add(checkBox);
        }

        content.Children.Add(new TextBlock
        {
            Text = "주간 주기 고정",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = "계정별로 주간 사용량이 초기화되면 짧은 읽기 전용 anchor 요청을 보냅니다.",
            Style = (Style)Resources["MutedText"],
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var anchorAccount in _configuration.Accounts)
        {
            var anchorRow = new StackPanel { Spacing = 3 };
            var anchorCheck = new CheckBox
            {
                Content = anchorAccount.Alias,
                IsChecked = _weeklyAnchorState.Preferences.IsEnabled(anchorAccount.Id),
            };
            var anchorStatus = new TextBlock
            {
                Text = WeeklyAnchorStatus(anchorAccount.Id),
                Style = (Style)Resources["MutedText"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(28, 0, 0, 0),
            };
            anchorCheck.Click += (_, _) =>
            {
                SetWeeklyAnchorEnabled(anchorAccount.Id, anchorCheck.IsChecked == true);
                anchorStatus.Text = WeeklyAnchorStatus(anchorAccount.Id);
            };
            anchorRow.Children.Add(anchorCheck);
            anchorRow.Children.Add(anchorStatus);

            var anchorNow = new Button
            {
                Content = "지금 anchor 보내기",
                Style = (Style)Resources["ActionButton"],
                Margin = new Thickness(28, 2, 0, 0),
                IsEnabled = !anchorAccount.IsPending && !anchorAccount.NeedsLogin,
            };
            anchorNow.Click += async (_, _) =>
            {
                SetBusy(true);
                try
                {
                    await StartWeeklyAnchorNowAsync(anchorAccount.Id);
                    anchorStatus.Text = WeeklyAnchorStatus(anchorAccount.Id);
                }
                finally
                {
                    SetBusy(false);
                }
            };
            anchorRow.Children.Add(anchorNow);
            content.Children.Add(anchorRow);
        }

        content.Children.Add(new TextBlock
        {
            Text = "새로고침 및 인증 유지",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        });
        var maintenanceStatus = new TextBlock
        {
            Text = _authMaintenanceState.LastFullSyncAt is { } lastSync
                ? $"마지막 전체 동기화: {lastSync.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                : "전체 동기화 기록이 없습니다.",
            Style = (Style)Resources["MutedText"],
            TextWrapping = TextWrapping.Wrap,
        };
        content.Children.Add(maintenanceStatus);
        var refreshActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        var selectedRefresh = new Button
        {
            Content = "선택 계정",
            Style = (Style)Resources["ActionButton"],
            IsEnabled = GetSelectedAccount() is { IsPending: false, NeedsLogin: false },
        };
        selectedRefresh.Click += async (_, _) =>
        {
            if (GetSelectedAccount() is not { } selected || selected.IsPending || selected.NeedsLogin)
            {
                return;
            }

            SetBusy(true);
            try
            {
                await LoadUsageAsync(selected.Id);
                maintenanceStatus.Text = "선택 계정 사용량을 갱신했습니다.";
            }
            finally
            {
                SetBusy(false);
            }
        };
        refreshActions.Children.Add(selectedRefresh);
        var allRefresh = new Button
        {
            Content = "모두 새로고침",
            Style = (Style)Resources["ActionButton"],
        };
        allRefresh.Click += async (_, _) =>
        {
            SetBusy(true);
            try
            {
                await RefreshAllUsageAsync();
                await RefreshDevicesAsync();
                maintenanceStatus.Text = "모든 계정과 장치 상태를 갱신했습니다.";
            }
            finally
            {
                SetBusy(false);
            }
        };
        refreshActions.Children.Add(allRefresh);
        var maintainNow = new Button
        {
            Content = "지금 인증 동기화",
            Style = (Style)Resources["ActionButton"],
        };
        maintainNow.Click += async (_, _) =>
        {
            SetBusy(true);
            try
            {
                await MaintainAuthAsync(forceFullSync: true);
                maintenanceStatus.Text = "인증 유지와 전체 장치 동기화를 확인했습니다.";
            }
            finally
            {
                SetBusy(false);
            }
        };
        refreshActions.Children.Add(maintainNow);
        content.Children.Add(refreshActions);

        content.Children.Add(new TextBlock
        {
            Text = "알림 영역에 표시할 항목(최대 2개)",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        });
        var menuItems = _menuBarUsagePreferences.NormalizedItems().ToList();
        var menuCount = new ComboBox
        {
            Header = "표시 개수",
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 180,
        };
        foreach (var count in new[] { "0개", "1개", "2개" })
        {
            menuCount.Items.Add(count);
        }

        var menuSlots = new StackPanel { Spacing = 6 };
        var menuPreview = new TextBlock
        {
            Style = (Style)Resources["MutedText"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        void RefreshMenuPreview()
        {
            var title = ((App)Application.Current).TrayIcon?.CurrentTitle ?? "Codex SyncBar";
            menuPreview.Text = $"미리보기: {title}";
        }

        var rebuildingMenuSlots = false;
        void SaveMenuItems(IEnumerable<UsageDisplayItem> items)
        {
            _menuBarUsagePreferences.SetItems(items);
            _usageDisplayStore.SaveMenuPreferences(_menuBarUsagePreferences);
            UpdateTrayTitle();
            if (_lastUsageSnapshot is not null)
            {
                RenderUsage(_lastUsageSnapshot);
            }
            RefreshMenuPreview();
        }

        void RebuildMenuSlots()
        {
            rebuildingMenuSlots = true;
            try
            {
                menuSlots.Children.Clear();
                menuItems = _menuBarUsagePreferences.NormalizedItems().ToList();
                for (var index = 0; index < menuItems.Count; index++)
                {
                    var slotIndex = index;
                    var row = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                    };
                    row.Children.Add(new TextBlock
                    {
                        Text = $"{slotIndex + 1}번",
                        Width = 42,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    var combo = new ComboBox { Width = 210 };
                    var other = menuItems
                        .Where((_, otherIndex) => otherIndex != slotIndex)
                        .ToHashSet();
                    var options = Enum.GetValues<UsageDisplayItem>()
                        .Where(item => !other.Contains(item) || item == menuItems[slotIndex])
                        .ToArray();
                    foreach (var option in options)
                    {
                        combo.Items.Add(new ComboBoxItem
                        {
                            Content = option.Title(),
                            Tag = option,
                        });
                    }

                    combo.SelectedIndex = Array.IndexOf(options, menuItems[slotIndex]);
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (rebuildingMenuSlots
                            || combo.SelectedItem is not ComboBoxItem { Tag: UsageDisplayItem selectedItem })
                        {
                            return;
                        }

                        var selected = _menuBarUsagePreferences.NormalizedItems().ToList();
                        if (slotIndex >= selected.Count)
                        {
                            return;
                        }

                        selected[slotIndex] = selectedItem;
                        SaveMenuItems(selected);
                        RebuildMenuSlots();
                    };
                    row.Children.Add(combo);
                    menuSlots.Children.Add(row);
                }
            }
            finally
            {
                rebuildingMenuSlots = false;
            }
        }

        menuCount.SelectedIndex = menuItems.Count;
        menuCount.SelectionChanged += (_, _) =>
        {
            if (menuCount.SelectedIndex < 0)
            {
                return;
            }

            var count = Math.Min(menuCount.SelectedIndex, MenuBarUsagePreferences.MaximumItemCount);
            var selected = menuItems.ToList();
            foreach (var item in new[]
                     {
                         UsageDisplayItem.CodexWeekly,
                         UsageDisplayItem.SparkWeekly,
                     }.Concat(Enum.GetValues<UsageDisplayItem>()))
            {
                if (selected.Count >= count)
                {
                    break;
                }

                if (!selected.Contains(item))
                {
                    selected.Add(item);
                }
            }

            SaveMenuItems(selected.Take(count));
            RebuildMenuSlots();
        };
        content.Children.Add(menuCount);
        content.Children.Add(menuSlots);
        content.Children.Add(menuPreview);
        RebuildMenuSlots();
        RefreshMenuPreview();
        var dialog = new ContentDialog
        {
            Title = "Codex SyncBar 설정",
            Content = new ScrollViewer { Content = content, MaxHeight = 420 },
            CloseButtonText = "닫기",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async void OpenCodexButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _localSwitchService.OpenCodex();
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
        }
        await Task.CompletedTask;
    }

    private void OpenCodexFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _localSwitchService.OpenCodexHome();
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
        }
    }

    private void OpenAuthFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _browserLoginService.OpenAuthFileFolder();
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
        }
    }

    private void QuitButton_Click(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).MainWindow?.ExitApplication();
    }

    private AccountProfile? GetSelectedAccount() => _configuration.Accounts.FirstOrDefault(
        account => account.Id == _selectedProfileId);

    private void MarkAccountNeedsLoginIfCanonicalFailure(int profileId, Exception error)
    {
        if (error is not AuthenticationRequiredException)
        {
            return;
        }

        MarkAccountNeedsLogin(profileId);
    }

    private void MarkAccountNeedsLogin(int profileId)
    {
        var account = _configuration.Accounts.FirstOrDefault(item => item.Id == profileId);
        if (account is null || account.IsPending || account.NeedsLogin)
        {
            return;
        }

        try
        {
            _configurationStore.MarkAccountNeedsLogin(_configuration, profileId);
            UpdateAccountsView(_selectedProfileId);
            UpdateTrayTitle();
        }
        catch (Exception error)
        {
            SetBanner($"계정 인증 상태를 저장하지 못했습니다: {error.Message}", isError: true);
        }
    }

    private void UpdateTrayTitle()
    {
        var activeProfileId = _localSwitchService.GetActiveProfileId(_configuration.Accounts);
        var profile = _configuration.Accounts.FirstOrDefault(account => account.Id == activeProfileId)
            ?? GetSelectedAccount();
        if (profile is null)
        {
            ((App)Application.Current).TrayIcon?.SetUsageTitle("Codex SyncBar");
            NotifyTrayStateChanged();
            return;
        }

        var hasDeviceMismatch = _deviceRows.Any(device =>
            !device.IsReachable || device.ProfileId != activeProfileId);
        _usageErrors.TryGetValue(profile.Id, out var failure);
        var title = MenuTitleFormatter.Title(
            profile,
            _usageSnapshots.GetValueOrDefault(profile.Id),
            failure,
            _menuBarUsagePreferences.NormalizedItems(),
            _activeUsageRefreshes > 0,
            hasDeviceMismatch);
        ((App)Application.Current).TrayIcon?.SetUsageTitle(title);
        NotifyTrayStateChanged();
    }

    private void UpdateLoginActions(AccountProfile? account)
    {
        var running = _loginCancellation is not null;
        var isLastLogin = account is not null && _lastLoginProfileId == account.Id;
        var canRecover = !_isBusy && !_configurationRecoveryNeeded && account is not null && isLastLogin;
        CancelLoginButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RetryLoginButton.Visibility = canRecover ? Visibility.Visible : Visibility.Collapsed;
        ReopenLoginButton.Visibility = canRecover ? Visibility.Visible : Visibility.Collapsed;
        FreshLoginButton.Visibility = !_isBusy && !_configurationRecoveryNeeded && account is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelLoginButton.IsEnabled = running;
        RetryLoginButton.IsEnabled = canRecover;
        ReopenLoginButton.IsEnabled = canRecover;
        FreshLoginButton.IsEnabled = !_isBusy && !_configurationRecoveryNeeded && account is not null;
    }

    private void UpdateBrowserCleanupActions()
    {
        var hasPending = _pendingBrowserCleanup.Count > 0;
        BrowserCleanupText.Text = hasPending
            ? $"Chrome 세션 정리 대기: {string.Join(", ", _pendingBrowserCleanup.Order())}"
            : string.Empty;
        BrowserCleanupText.Visibility = hasPending
            ? Visibility.Visible
            : Visibility.Collapsed;
        RetryBrowserCleanupButton.Visibility = hasPending
            ? Visibility.Visible
            : Visibility.Collapsed;
        RetryBrowserCleanupButton.IsEnabled = hasPending && !_isBusy && !_configurationRecoveryNeeded;
    }

    private async void RetryBrowserCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            _pendingBrowserCleanup = _browserLoginService.RecoverPendingProfiles().ToHashSet();
            UpdateBrowserCleanupActions();
            SetBanner(
                _pendingBrowserCleanup.Count == 0
                    ? "대기 중이던 Chrome 세션 정리를 완료했습니다."
                    : $"Chrome 세션 정리가 아직 필요합니다: {string.Join(", ", _pendingBrowserCleanup.Order())}",
                isError: _pendingBrowserCleanup.Count > 0);
        }
        catch (Exception error)
        {
            SetBanner($"Chrome 세션 정리 재시도에 실패했습니다: {error.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
            UpdateBrowserCleanupActions();
        }
    }

    private void RemoveAccountState(int profileId)
    {
        _usageSnapshots.Remove(profileId);
        _usageErrors.Remove(profileId);
        _weeklyAnchorState.Preferences.SetEnabled(profileId, false);
        _weeklyAnchorState.Records.Remove(profileId);
        _weeklyAnchorStore.Save(_weeklyAnchorState);
    }

    private bool IsCursorProviderActive()
    {
        try
        {
            return _codexConfigService.ActiveConfiguration() is not null;
        }
        catch
        {
            return false;
        }
    }

    private void EditAliasButtonIsEnabled(bool enabled)
    {
        var button = FindName("EditAliasButton");
        if (button is Button aliasButton)
        {
            aliasButton.IsEnabled = enabled;
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        LoadingRing.IsActive = busy;
        var selectedAccount = GetSelectedAccount();
        var selectedHasCredentials = selectedAccount is not null
            && !selectedAccount.IsPending
            && !selectedAccount.NeedsLogin
            && _authStore.ProfileArtifactExists(selectedAccount.Id);
        var canMutate = !busy && !_configurationRecoveryNeeded;
        RefreshButton.IsEnabled = !busy;
        AddAccountButtonIsEnabled(canMutate);
        RefreshSelectedButton.IsEnabled = canMutate && selectedHasCredentials;
        LoginButton.IsEnabled = canMutate && selectedAccount is not null;
        ImportAuthButton.IsEnabled = canMutate && selectedAccount is not null;
        LogoutAccountButton.IsEnabled = canMutate
            && _configuration.Accounts.Count > 1
            && selectedHasCredentials;
        SyncButton.IsEnabled = canMutate && selectedHasCredentials;
        DeleteAccountButton.IsEnabled = canMutate
            && _configuration.Accounts.Count > 1
            && selectedAccount is not null
            && !_authStore.ProfileArtifactExists(selectedAccount.Id);
        EditAliasButtonIsEnabled(canMutate && selectedAccount is not null);
        CursorEnableButton.IsEnabled = canMutate;
        CursorDisableButton.IsEnabled = canMutate && IsCursorProviderActive();
        CursorSaveApiKeyButton.IsEnabled = canMutate;
        CursorDeleteApiKeyButton.IsEnabled = canMutate && _cursorApiKeyStore.HasKey;
        CursorSyncRemoteButton.IsEnabled = canMutate
            && IsCursorProviderActive()
            && _cursorApiKeyStore.HasKey;
        UpdateAccountOrderActions();
        UpdateDeviceActions();
        UpdateLoginActions(selectedAccount);
        UpdateBrowserCleanupActions();
        NotifyTrayStateChanged();
    }

    private void UpdateAccountOrderActions()
    {
        var index = _configuration.Accounts.FindIndex(item => item.Id == _selectedProfileId);
        MoveAccountUpButton.IsEnabled = !_isBusy && !_configurationRecoveryNeeded && index > 0;
        MoveAccountDownButton.IsEnabled = !_isBusy && !_configurationRecoveryNeeded
            && index >= 0
            && index < _configuration.Accounts.Count - 1;
    }

    private void AddAccountButtonIsEnabled(bool enabled)
    {
        var button = FindName("AddAccountButton");
        if (button is Button addButton)
        {
            addButton.IsEnabled = enabled;
        }
    }

    private void SetBanner(string message, bool isError)
    {
        _bannerMessage = message;
        _bannerIsError = isError;
        BannerText.Text = message;
        BannerText.Foreground = new SolidColorBrush(isError ? Colors.OrangeRed : Colors.LightGray);
        NotifyTrayStateChanged();
    }

    private void NotifyTrayStateChanged() =>
        TrayStateChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class DeviceRow
{
    public DeviceRow(DeviceStatus status, SshDeviceConfiguration? configured)
    {
        Id = status.Id;
        DisplayName = status.DisplayName;
        Detail = status.Detail ?? (status.Id == "windows" ? "이 장치" : "SSH 장치");
        IsReachable = status.IsReachable;
        ProfileId = status.ProfileId;
        IsConfigured = configured is not null;
        IsEnabled = configured?.Enabled == true;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Detail { get; }

    public bool IsReachable { get; }

    public int? ProfileId { get; }

    public bool IsConfigured { get; }

    public bool IsEnabled { get; }

    public string StateText => !IsEnabled && IsConfigured
        ? "비활성"
        : IsReachable ? "연결됨" : "오프라인";

    public string StateGlyph => IsReachable ? "✓" : "!";

    public Brush StateBrush => new SolidColorBrush(IsReachable ? Colors.LimeGreen : Colors.OrangeRed);
}

public sealed class TokenUsageRow
{
    public TokenUsageRow(DeviceTokenUsage usage, AccountProfile? profile)
    {
        DisplayName = usage.DisplayName;
        AccountText = usage.Error is not null
            ? "연결 또는 세션 수집 실패"
            : profile?.Alias ?? (usage.IsReachable ? "적용 계정 확인 중" : "연결 안 됨");
        AccountBrush = usage.Error is not null || !usage.IsReachable
            ? new SolidColorBrush(Colors.OrangeRed)
            : profile is null
                ? new SolidColorBrush(Colors.Gold)
                : new SolidColorBrush(Colors.LimeGreen);

        if (usage.Summary is { } summary)
        {
            UsageText = $"{TokenUsageFormatting.Tokens(summary.TotalTokens)} · {summary.Requests:N0} requests";
            CostText = usage.UnpricedTokens > 0
                ? $"{TokenUsageFormatting.Dollars(usage.EstimatedCostUsd)} + 미가격 {TokenUsageFormatting.Tokens(usage.UnpricedTokens)}"
                : TokenUsageFormatting.Dollars(usage.EstimatedCostUsd);
        }
        else
        {
            UsageText = "사용량 수집 실패";
            CostText = usage.Error ?? "확인 필요";
        }
    }

    public string DisplayName { get; }

    public string AccountText { get; }

    public Brush AccountBrush { get; }

    public string UsageText { get; }

    public string CostText { get; }
}
