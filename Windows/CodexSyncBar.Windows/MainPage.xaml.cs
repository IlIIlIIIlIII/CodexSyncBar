using System.Collections.ObjectModel;
using CodexSyncBar.Windows.Core;
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
    private readonly LocalSwitchService _localSwitchService;
    private readonly BrowserLoginService _browserLoginService;
    private readonly CodexLoginService _codexLoginService;
    private readonly CodexAuthMaintenanceService _authMaintenanceService;
    private readonly SshDeviceService _sshDeviceService;
    private readonly TokenUsageService _tokenUsageService;
    private readonly WslDeviceService _wslDeviceService;
    private readonly WslConfigurationStore _wslConfigurationStore;
    private readonly UsageDisplayPreferencesStore _usageDisplayStore;
    private readonly SelectedProfileStore _selectedProfileStore;
    private readonly WeeklyAnchorStore _weeklyAnchorStore;
    private readonly AuthMaintenanceStateStore _authMaintenanceStateStore;
    private readonly DeviceActivationTransactionStore _deviceActivationTransactions;
    private readonly LoginTransactionStore _loginTransactions;
    private readonly ObservableCollection<DeviceRow> _deviceRows = [];
    private readonly ObservableCollection<TokenUsageRow> _tokenUsageRows = [];

    private AppConfiguration _configuration = new();
    private UsageDisplayPreferences _usageDisplayPreferences = new();
    private MenuBarUsagePreferences _menuBarUsagePreferences = new();
    private UsageSnapshot? _lastUsageSnapshot;
    private readonly Dictionary<int, UsageSnapshot> _usageSnapshots = [];
    private readonly Dictionary<int, string> _usageErrors = [];
    private HashSet<int> _pendingBrowserCleanup = [];
    private WeeklyAnchorState _weeklyAnchorState = new();
    private AuthMaintenanceState _authMaintenanceState = new();
    private readonly TaskCompletionSource<bool> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int? _selectedProfileId;
    private string? _selectedDeviceId;
    private DispatcherTimer? _deviceTimer;
    private DispatcherTimer? _resetCreditsTimer;
    private bool _isBusy;
    private bool _controllerBusy;
    private bool _hasLoaded;
    private bool _configurationRecoveryNeeded;
    private int _activeUsageRefreshes;
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
        _wslDeviceService = new WslDeviceService(_paths, _authStore);
        _wslConfigurationStore = new WslConfigurationStore(_paths);
        _usageDisplayStore = new UsageDisplayPreferencesStore(_paths);
        _selectedProfileStore = new SelectedProfileStore(_paths);
        _weeklyAnchorStore = new WeeklyAnchorStore(_paths);
        _authMaintenanceStateStore = new AuthMaintenanceStateStore(_paths);
        _deviceActivationTransactions = new DeviceActivationTransactionStore(_paths);
        DevicesList.ItemsSource = _deviceRows;
        TokenUsageDevicesList.ItemsSource = _tokenUsageRows;

    }

    private Task? _initialization;

    // Explicit initialization also runs for hidden background launches. Widget
    // requests never depend on the XAML Loaded event firing.
    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        Controller.Changed += Controller_Changed;
        try { await LoadAsync(); }
        finally { _ready.TrySetResult(true); }
    }

    private SyncBarController Controller => ((App)Application.Current).Controller
        ?? throw new InvalidOperationException("계정 제어 서비스를 준비하지 못했습니다.");

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
            var recovery = Controller.RecoveryStatus;
            var pendingLogoutRecovery = recovery?.Logout ?? new LogoutRecoveryResult([], []);
            var pendingSecretCleanup = recovery?.PendingSecretCleanup ?? [];
            var pendingBootstrapRecovery = recovery?.PendingBootstrapOperations ?? [];
            var pendingBrowserCleanup = recovery?.PendingBrowserCleanup ?? [];
            _pendingBrowserCleanup = pendingBrowserCleanup.ToHashSet();
            UpdateBrowserCleanupActions();
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

                SetBanner(
                    pendingLogoutRecovery.PendingOperations.Count == 0
                    && pendingBrowserCleanup.Count == 0
                    && pendingSecretCleanup.Count == 0
                    && pendingBootstrapRecovery.Count == 0
                    ? _configuration.Accounts.Count == 0 ? "계정 추가를 눌러 첫 번째 Codex 계정을 연결해 주세요." : "준비되었습니다."
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
            && !_isBusy && !_controllerBusy
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
            _isBusy || _controllerBusy || _activeUsageRefreshes > 0 || _loginCancellation is not null,
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

    public Task ShutdownAsync()
    {
        Controller.Changed -= Controller_Changed;
        _deviceTimer?.Stop();
        _resetCreditsTimer?.Stop();
        _loginCancellation?.Cancel();
        if (_lastLoginProfileId is { } profileId)
        {
            _browserLoginService.CloseLoginWindow(profileId);
        }
        return Task.CompletedTask;
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
        if (_deviceTimer is not null) return;
        // Shared controller owns usage/device/auth polling for both UI surfaces.
        _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _deviceTimer.Tick += async (_, _) => { if (!_isBusy) await RefreshTokenUsageAsync(); };
        _deviceTimer.Start();
        _resetCreditsTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _resetCreditsTimer.Tick += (_, _) =>
        {
            if (_lastUsageSnapshot is { } snapshot && snapshot.ProfileId == _selectedProfileId)
            {
                RenderResetCredits(snapshot);
                RenderQuota(snapshot.Session, FiveHourBar, FiveHourValue, FiveHourResetText);
                RenderQuota(snapshot.Weekly, WeeklyBar, WeeklyValue, WeeklyResetText);
                NotifyTrayStateChanged();
            }
        };
        _resetCreditsTimer.Start();
    }

    private void Controller_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(async () =>
    {
        try
        {
            var dashboard = await Controller.GetSnapshotAsync();
            _weeklyAnchorState = Controller.WeeklyAnchor.Load();
            _controllerBusy = dashboard.IsBusy;
            RetryRecoveryButton.Visibility = dashboard.Operation?.State == "recoveryRequired" ? Visibility.Visible : Visibility.Collapsed;
            if (dashboard.Operation is { } operation)
                SetBanner(operation.Message, operation.State is "failed" or "recoveryRequired");
            RenderDashboardDevices(dashboard);
            UpdateAppliedAccount(dashboard.ActiveProfileId, GetSelectedAccount());
            foreach (var account in _configuration.Accounts)
            {
                CopyCachedUsage(account.Id);

            }
            SetBusy(_isBusy);
        }
        catch (Exception error) { SetBanner(error.Message, true); }
    });

    private void CopyCachedUsage(int profileId)
    {
        if (Controller.GetUsageError(profileId) is { } error) _usageErrors[profileId] = error;
        else _usageErrors.Remove(profileId);
        if (Controller.TryGetUsageSnapshot(profileId) is not { } usage) return;
        _usageSnapshots[profileId] = usage;
        if (profileId != _selectedProfileId) return;
        _lastUsageSnapshot = usage;
        RenderUsage(usage);
        UsageStatusText.Text = _usageErrors.GetValueOrDefault(profileId) ?? "사용량을 확인했습니다.";
        AuthStatusText.Text = _usageErrors.ContainsKey(profileId) ? "확인 필요" : "인증 정상";
        SelectedPlanText.Text = usage.Plan;
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
        SelectedPlanText.Text = "Codex";
        UpdateAppliedAccount(activeProfileId, account);
        AuthStatusText.Text = account is null
            ? "인증 확인 전"
            : account.IsPending
                ? "로그인 필요"
                : account.NeedsLogin
                    ? "로그아웃됨 · 재로그인 필요"
                    : "인증 확인 중…";
        LoginButton.Content = account?.IsPending == true ? "로그인 열기" : "재로그인";
        ImportAuthButton.Text = account?.IsPending == true ? "인증 파일 가져오기" : "인증 파일 교체";
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

    private void UpdateAppliedAccount(int? activeProfileId, AccountProfile? account)
    {
        var activeAccount = _configuration.Accounts.FirstOrDefault(item => item.Id == activeProfileId);
        CurrentAccountText.Text = activeAccount is null
            ? "현재 적용된 계정 없음"
            : $"현재 적용 · {activeAccount.Alias}";
        ActiveBadgeText.Text = account is null
            ? "계정을 추가하고 로그인해 주세요."
            : activeProfileId == account.Id
                ? "선택한 계정이 현재 적용되어 있어요."
                : "적용을 누르면 선택한 계정으로 전환합니다.";
    }

    private async Task LoadUsageAsync(int profileId, bool retriedAfterRefresh = false)
    {
        var account = _configuration.Accounts.FirstOrDefault(item => item.Id == profileId);
        if (account is null || account.IsPending || account.NeedsLogin)
        {
            ResetUsageView();
            AuthStatusText.Text = "로그인 필요";
            UsageStatusText.Text = "계정 추가 또는 로그인 열기를 눌러 연결해 주세요.";
            return;
        }
        try
        {
            CopyCachedUsage(profileId);
            var cached = Controller.TryGetUsageSnapshot(profileId);
            if (cached is null || DateTimeOffset.UtcNow - cached.UpdatedAt > TimeSpan.FromSeconds(30))
                await RefreshAllUsageAsync();
            CopyCachedUsage(profileId);
            if (Controller.GetUsageError(profileId) is { } error)
                UsageStatusText.Text = error;
        }
        catch (Exception error) { SetBanner(error.Message, true); }
    }

    private async Task RefreshAllUsageAsync()
    {
        _activeUsageRefreshes++;
        UpdateTrayTitle();
        try
        {
            await Controller.RefreshUsageAsync();
            foreach (var account in _configuration.Accounts.ToArray())
            {
                CopyCachedUsage(account.Id);
            }
        }
        catch (Exception error) { SetBanner(error.Message, true); }
        finally { _activeUsageRefreshes--; UpdateTrayTitle(); }
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
            $"{TokenUsageFormatting.Tokens(counts.TotalTokens)} 토큰 · 요청 {requests:N0}회";
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

    private void DetailContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Cards respond to the space left after the account pane and scroll bar.
        // Window-wide breakpoints alone are incorrect when the sidebar is visible.
        var sideBySide = e.NewSize.Width >= 1000;
        ArrangeCardPair(OverviewCards, OverviewSecondaryColumn, ResetCreditsCard, sideBySide);
        ArrangeCardPair(DeviceAndTokenCards, DeviceSecondaryColumn, TokensCard, sideBySide);
    }

    private static void ArrangeCardPair(Grid grid, ColumnDefinition secondColumn, Border secondCard, bool sideBySide)
    {
        secondColumn.Width = sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        grid.ColumnSpacing = sideBySide ? 24 : 0;
        grid.RowSpacing = sideBySide ? 0 : 24;
        Grid.SetColumn(secondCard, sideBySide ? 1 : 0);
        Grid.SetRow(secondCard, sideBySide ? 0 : 1);
    }

    private void UsageMetrics_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateQuotaLayout();

    private void UpdateQuotaLayout()
    {
        var showSession = FiveHourRow.Visibility == Visibility.Visible;
        var showWeekly = CodexWeeklyRow.Visibility == Visibility.Visible;
        var paired = showSession && showWeekly && UsageMetrics.ActualWidth >= 520;
        UsageMetrics.Visibility = showSession || showWeekly ? Visibility.Visible : Visibility.Collapsed;
        WeeklyMetricColumn.Width = paired ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        UsageMetrics.ColumnSpacing = paired ? 24 : 0;
        Grid.SetColumn(CodexWeeklyRow, paired ? 1 : 0);
        Grid.SetRow(CodexWeeklyRow, showSession && !paired ? 1 : 0);
        CodexWeeklyRow.Margin = showSession && !paired ? new Thickness(0, 24, 0, 0) : new Thickness(0);
    }

    private void RenderUsage(UsageSnapshot snapshot)
    {
        FiveHourRow.Visibility = snapshot.Session is not null && _usageDisplayPreferences.IsVisible(UsageDisplayItem.FiveHour)
            ? Visibility.Visible
            : Visibility.Collapsed;
        CodexWeeklyRow.Visibility = _usageDisplayPreferences.IsVisible(UsageDisplayItem.CodexWeekly)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RenderQuota(snapshot.Session, FiveHourBar, FiveHourValue, FiveHourResetText);
        RenderQuota(snapshot.Weekly, WeeklyBar, WeeklyValue, WeeklyResetText);
        UpdateQuotaLayout();

        UsageStatusText.Text = "사용량을 확인했습니다.";
        UsageUpdatedText.Text = $"갱신 {snapshot.UpdatedAt.ToLocalTime():HH:mm:ss}";
        CreditsText.Text = snapshot.UnlimitedCredits
            ? "추가 크레딧: 무제한"
            : snapshot.CreditBalance.HasValue
                ? $"추가 크레딧: {snapshot.CreditBalance.Value:0.##}"
                : "추가 크레딧: 확인되지 않음";
        RenderResetCredits(snapshot);
        UpdateTrayTitle();
    }

    private void RenderResetCredits(UsageSnapshot snapshot)
    {
        ResetCreditsCountText.Text = snapshot.ResetCredits.HasValue
            ? $"{snapshot.ResetCredits.Value}개"
            : "—";
        var groups = snapshot.ResetCredits == 0
            ? Array.Empty<ResetCreditExpiryGroup>()
            : UsageFormatting.ResetCreditExpiryGroups(snapshot.ResetCreditExpirations, DateTimeOffset.UtcNow);
        ResetCreditGroups.ItemsSource = groups;
        ResetCreditGroups.Visibility = groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var listedCount = groups.Sum(group => group.Count);
        ResetCreditsStatusText.Text = snapshot.ResetCredits switch
        {
            null => "초기화권 수량을 확인하지 못했습니다.",
            0 => "현재 보유한 초기화권이 없습니다.",
            _ when groups.Count == 0 => "만료 정보가 제공되지 않았습니다.",
            var count when count > listedCount => $"{count - listedCount}개의 만료 정보는 확인되지 않았습니다.",
            _ => string.Empty,
        };
        ResetCreditsStatusText.Visibility = string.IsNullOrEmpty(ResetCreditsStatusText.Text)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void RenderQuota(
        UsageWindow? window,
        ProgressBar bar,
        TextBlock value,
        TextBlock reset)
    {
        bar.Visibility = window is null ? Visibility.Collapsed : Visibility.Visible;
        bar.Value = window is null ? 0 : Math.Clamp(window.RemainingPercent, 0, 100);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(bar,
            window is null ? "한도 정보 없음" : $"{Math.Round(window.RemainingPercent):0}% 남음");
        value.Text = window is null
            ? "—"
            : $"{Math.Round(window.RemainingPercent):0}% 남음";
        reset.Text = window is null
            ? "한도 정보 없음"
            : UsageFormatting.QuotaResetDescription(window.ResetsAt, DateTimeOffset.UtcNow);
        bar.Opacity = window is null ? 0.25 : 1;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(bar,
            window is null ? "한도 정보 없음" : $"{value.Text}, {reset.Text}");
    }

    private string WeeklyAnchorStatus(int profileId)
    {
        var record = _weeklyAnchorState.Records.GetValueOrDefault(profileId);
        if (Controller.WeeklyAnchor.IsRunning(profileId)) return "메시지 전송 중…";
        if (!_weeklyAnchorState.Preferences.IsEnabled(profileId))
        {
            return record?.LastError is not null ? "자동 전송 꺼짐 · 최근 전송 실패"
                : record?.LastSuccessAt is { } sent ? $"자동 전송 꺼짐 · 최근 전송 {sent.ToLocalTime():MM-dd HH:mm}"
                : "사용 안 함";
        }

        if (!string.IsNullOrWhiteSpace(record?.LastError))
        {
            if (record.LastAttemptAt is { } lastAttempt)
            {
                var retryAt = lastAttempt + WeeklyAnchorDecisionEngine.RetryInterval;
                if (WeeklyAnchorDecisionEngine.RetryIsCoolingDown(record, DateTimeOffset.UtcNow))
                {
                    return $"실행 실패 · {UsageFormatting.ResetCreditExpiryDescription(retryAt)} 후 재시도";
                }
            }

            return "실행 실패 · 다음 확인 때 재시도";
        }

        if (Controller.WeeklyAnchor.IsRunning(profileId))
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
        try
        {
            var sent = await Controller.SendWeeklyAnchorNowAsync(profileId);
            SetBanner(sent ? "주간 주기 시작 메시지를 보냈습니다." : "이미 전송 중이거나 최신 주간 사용량이 없습니다.", !sent);
        }
        catch (Exception error)
        {
            MarkAccountNeedsLoginIfCanonicalFailure(profileId, error);
            SetBanner($"주간 메시지 전송 실패: {error.Message}", true);
        }
        _weeklyAnchorState = Controller.WeeklyAnchor.Load();
    }
    private void ResetUsageView()
    {
        _lastUsageSnapshot = null;
        foreach (var bar in new[] { FiveHourBar, WeeklyBar })
        {
            bar.Value = 0;
            bar.Opacity = 0.25;
            bar.Visibility = Visibility.Collapsed;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(bar, "한도 정보 없음");
        }
        foreach (var text in new[] { FiveHourValue, WeeklyValue })
        {
            text.Text = "—";
        }
        foreach (var text in new[] { FiveHourResetText, WeeklyResetText })
        {
            text.Text = string.Empty;
        }
        FiveHourRow.Visibility = Visibility.Collapsed;
        CodexWeeklyRow.Visibility = _usageDisplayPreferences.IsVisible(UsageDisplayItem.CodexWeekly)
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateQuotaLayout();
        CreditsText.Text = string.Empty;
        ResetCreditsCountText.Text = "—";
        ResetCreditsStatusText.Text = "초기화권 정보를 확인하기 전입니다.";
        ResetCreditsStatusText.Visibility = Visibility.Visible;
        ResetCreditGroups.ItemsSource = null;
        ResetCreditGroups.Visibility = Visibility.Collapsed;
        UsageUpdatedText.Text = string.Empty;
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            await Controller.RefreshDevicesAsync();
            RenderDashboardDevices(await Controller.GetSnapshotAsync());
        }
        catch (Exception error) { SetBanner($"장치 상태를 확인하지 못했습니다: {error.Message}", true); }
    }

    private void RenderDashboardDevices(DashboardSnapshot dashboard)
    {
        var selectedDeviceId = _selectedDeviceId;
        _deviceRows.Clear();
        var wsl = _wslConfigurationStore.Load();
        foreach (var device in dashboard.Devices)
        {
            var id = device.Kind == "ssh" && device.Id.StartsWith("ssh:") ? device.Id[4..] : device.Id;
            var configured = _configuration.Devices.FirstOrDefault(item => item.Id == id);
            var status = new DeviceStatus(id, device.DisplayName, device.ProfileId, null,
                device.Kind, null, device.IsReachable, device.Detail);
            _deviceRows.Add(new DeviceRow(status, configured,
                device.Kind == "wsl" ? wsl.FirstOrDefault(item => item.Id == id)?.Enabled : null));
        }
        foreach (var device in _configuration.Devices.Where(d => !d.Enabled))
            _deviceRows.Add(new DeviceRow(new DeviceStatus(device.Id, device.DisplayLabel, null, null,
                "ssh", null, false, "설치 및 활성화가 필요합니다."), device));
        foreach (var device in wsl.Where(d => !d.Enabled))
            _deviceRows.Add(new DeviceRow(new DeviceStatus(device.Id, device.Distribution, null, null,
                "wsl", null, false, "WSL 관리에서 다시 활성화할 수 있습니다."), null, false));
        DevicesList.SelectedItem = _deviceRows.FirstOrDefault(row => row.Id == selectedDeviceId);
        UpdateDeviceActions();
        UpdateTrayTitle();
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
        TestDeviceButton.IsEnabled = canEdit;
        ManageCliButton.IsEnabled = !_isBusy && !_configurationRecoveryNeeded;
        UpdateAllCliButton.IsEnabled = !_isBusy && !_configurationRecoveryNeeded;
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
            if (!await EnsureSshTrustAsync(device)) return;
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
            SetBanner($"{device.DisplayLabel} 설치와 활성화를 완료했습니다.", isError: false);
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
        if (device is null)
        {
            return;
        }

        SetBusy(true);
        try
        {
            if (!await EnsureSshTrustAsync(device)) return;
            var result = await _sshDeviceService.TestConnectionAsync(device);
            SetBanner(result.Message, isError: !result.IsReachable);
            await RefreshDevicesAsync();
        }
        catch (Exception error)
        {
            SetBanner($"SSH 연결 테스트 실패: {error.Message}", isError: true);
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

    private async void RetryRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try { await Controller.RetryRecoveryAsync(); }
        catch (Exception error) { SetBanner(error.Message, true); }
        finally { SetBusy(false); }
        await LoadAsync();
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
            await RefreshAllUsageAsync();
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
        try
        {
            bool loginCompleted;
            using (var mutationLock = await ControllerMutationLock.AcquireAsync(_paths))
            {
                var account = _configurationStore.ReserveAccount(_configuration);
                UpdateAccountsView(account.Id);
                loginCompleted = await RunLoginCoreAsync(account, replaceExisting: false);
                if (account.IsPending
                    && !_authStore.ProfileArtifactExists(account.Id)
                    && _configuration.Accounts.Count > 1)
                {
                    _configurationStore.RemoveAccount(_configuration, account.Id);
                    RemoveAccountState(account.Id);
                    UpdateAccountsView(_configuration.Accounts.FirstOrDefault()?.Id);
                }
            }
            if (loginCompleted) await RefreshAllUsageAsync();
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
        }
    }

    private async void MoveAccountButton_Click(object sender, RoutedEventArgs e)
    {
        var account = GetSelectedAccount();
        var direction = (sender as FrameworkElement)?.Tag?.ToString();
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
            var syncFailures = await SyncProfileToDevicesAsync(account.Id);
            mutationLock.Dispose();
            await LoadUsageAsync(account.Id);
            SetBanner(
                syncFailures.Count == 0
                    ? "인증 정보를 안전하게 등록하고 활성 장치에 동기화했습니다."
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
            SetBanner("계정별 Chrome 프로필에서 로그인 페이지를 다시 열었습니다.", isError: false);
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

        SetBanner("계정별 Chrome 프로필에서 다른 계정 로그인을 엽니다…", isError: false);
        await RunLoginAsync(account, replaceExisting: true);
    }

    private async Task RunLoginAsync(AccountProfile account, bool replaceExisting)
    {
        try
        {
            bool loginCompleted;
            using (var mutationLock = await ControllerMutationLock.AcquireAsync(_paths))
            {
                loginCompleted = await RunLoginCoreAsync(account, replaceExisting);
            }
            if (loginCompleted) await RefreshAllUsageAsync();
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
        }
    }

    // Both callers own the mutation lease through login, account persistence and
    // device synchronization, then release it before usage can refresh credentials.
    private async Task<bool> RunLoginCoreAsync(AccountProfile account, bool replaceExisting)
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
            var syncFailures = await SyncProfileToDevicesAsync(account.Id);
            SetBanner(
                syncFailures.Count == 0
                    ? "로그인과 인증 저장, 활성 장치 동기화가 완료되었습니다."
                    : $"로그인은 완료했지만 일부 SSH 장치 동기화가 보류되었습니다: {string.Join(" · ", syncFailures)}",
                isError: syncFailures.Count > 0);
            _lastLoginProfileId = null;
            return true;
        }
        catch (OperationCanceledException)
        {
            SetBanner("로그인을 취소했습니다.", isError: false);
            return false;
        }
        catch (Exception error)
        {
            SetBanner(error.Message, isError: true);
            return false;
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
            Header = "계정 별칭",
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
            Content = $"{account.Alias} 계정 항목을 제거할까요? 이 계정에 연결된 Chrome 프로필도 정리합니다.",
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
            mutationLock.Dispose();
            await LoadUsageAsync(_selectedProfileId ?? 0);
            SetBanner(
                browserWarning is null
                    ? "계정 항목을 제거했습니다."
                    : $"계정 항목은 제거했지만 계정별 Chrome 세션 정리가 필요합니다: {browserWarning}",
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
            Content = $"{account.Alias} 계정을 이 PC와 모든 활성 WSL·SSH 장치에서 로그아웃할까요? 계정 항목은 유지되어 나중에 재로그인할 수 있습니다.",
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
            var operation = await Controller.LogoutAccountAsync(account.Id, fallback.Id);
            if (operation.State != "completed")
            {
                SetBanner(operation.Message, true);
                return;
            }
            _configuration = _configurationStore.LoadOrCreate();
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

            _usageSnapshots.Remove(account.Id);
            _usageErrors[account.Id] = "로그아웃되었습니다.";
            UpdateAccountsView(account.Id);
            mutationLock.Dispose();
            await LoadUsageAsync(account.Id);
            SetBanner(
                browserWarning is null
                    ? $"{account.Alias} 계정을 로그아웃했습니다. 계정 항목은 유지됩니다."
                    : $"로그아웃했지만 계정별 Chrome 세션 정리가 필요합니다: {browserWarning}",
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
            var snapshot = await Controller.GetSnapshotAsync();
            var operation = await Controller.SwitchAccountAsync(account.Id, snapshot.ConfigurationRevision);
            while (!operation.IsComplete)
            {
                SetBanner(operation.Message, isError: false);
                await Task.Delay(250);
                operation = await Controller.GetOperationStatusAsync(operation.Id) ?? operation;
            }
            await RefreshDevicesAsync();
            UpdateSelectedAccount(account);
            await LoadUsageAsync(account.Id);
            SetBanner(operation.Message, isError: operation.State != "completed");
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

    private async Task<IReadOnlyList<string>> SyncProfileToDevicesAsync(int profileId)
    {
        var failures = (await _sshDeviceService.SyncProfileAsync(_configuration, profileId)).ToList();
        var running = await _wslDeviceService.DiscoverAsync();
        foreach (var device in _wslConfigurationStore.Load().Where(device => device.Enabled
                     && running.Any(state => state.Name == device.Distribution && state.IsRunning)))
        {
            try { await _wslDeviceService.SyncAuthAsync(device, profileId); }
            catch (Exception error) { failures.Add($"{device.Distribution}: {error.Message}"); }
        }
        return failures;
    }

    private async void WslSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetBusy(true);
        try
        {
            var distributions = await _wslDeviceService.DiscoverAsync();
            if (distributions.Count == 0)
            {
                SetBanner("사용 가능한 WSL 배포판이 없습니다. Windows에서 WSL 배포판을 먼저 설치해 주세요.", true);
                return;
            }
            var configurations = _wslConfigurationStore.Load().ToList();
            var picker = new ComboBox
            {
                Header = "WSL 배포판", HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = distributions, DisplayMemberPath = "Name", SelectedIndex = 0,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(picker, "WslDistributionPicker");
            var user = new TextBox { Header = "Linux 사용자 (선택)", PlaceholderText = "비워 두면 배포판의 기본 사용자" };
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
            void UpdateSelection()
            {
                var selected = (WslDistribution)picker.SelectedItem;
                var configured = configurations.FirstOrDefault(d => d.Distribution == selected.Name);
                user.Text = configured?.Username ?? "";
                status.Text = $"{(selected.IsRunning ? "실행 중" : "정지됨")} · {(configured?.Enabled == true ? "계정 전환에 포함됨" : "아직 전환에 포함되지 않음")}";
            }
            picker.SelectionChanged += (_, _) => UpdateSelection();
            UpdateSelection();
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = "설치 및 활성화하면 배포판을 시작해 helper와 인증을 설치합니다. Linux의 bash, jq, node가 필요합니다. 정지된 배포판은 자동 상태 확인으로 시작되지 않습니다.",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(picker);
            content.Children.Add(user);
            content.Children.Add(status);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "WSL 장치 관리", Content = content,
                PrimaryButtonText = "설치 및 활성화", SecondaryButtonText = "전환 대상에서 제외",
                CloseButtonText = "닫기", DefaultButton = ContentDialogButton.Close,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None) return;
            var distribution = ((WslDistribution)picker.SelectedItem).Name;
            using var mutationLock = await ControllerMutationLock.AcquireAsync(_paths);
            // Reload while holding the same lock as widget and tray mutations.
            configurations = _wslConfigurationStore.Load().ToList();
            var device = configurations.FirstOrDefault(d => d.Distribution == distribution)
                ?? new WslDeviceConfiguration { Distribution = distribution };
            if (result == ContentDialogResult.Primary)
            {
                device.Username = user.Text.Trim();
                await _wslDeviceService.BootstrapAsync(device, _configuration.Accounts,
                    _localSwitchService.GetActiveProfileId(_configuration.Accounts));
                if (!configurations.Contains(device)) configurations.Add(device);
                _wslConfigurationStore.Save(configurations);
                SetBanner($"{distribution} 설치와 계정 동기화를 검증했습니다. 모든 장치 적용에 포함됩니다.", false);
            }
            else
            {
                device.Enabled = false;
                _wslConfigurationStore.Save(configurations);
                SetBanner($"{distribution}을 계정 전환 대상에서 제외했습니다.", false);
            }
            await RefreshDevicesAsync();
        }
        catch (Exception error) { SetBanner(error.Message, true); }
        finally { SetBusy(false); }
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

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_configurationRecoveryNeeded)
        {
            SetBanner("설정 복구가 필요합니다. 먼저 새로고침을 다시 시도해 주세요.", isError: true);
            return;
        }

        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(new TextBlock
        {
            Text = $"Codex SyncBar {typeof(App).Assembly.GetName().Version?.ToString(3) ?? "development"}",
            Style = (Style)Resources["MutedText"],
        });
        content.Children.Add(new Expander
        {
            Header = "저장 위치와 계정 보호",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new TextBlock
            {
                Text = $"계정 인증과 SSH 암호는 이 Windows 사용자만 열 수 있도록 암호화해 보관합니다.\n\n설정·프로필: {_paths.StateRoot}\nCodex: {_paths.CodexHome}\nChrome 프로필: {_paths.ChromeProfilesDirectory}",
                Style = (Style)Resources["MutedText"],
                TextWrapping = TextWrapping.Wrap,
            },
        });
        var launchAtLogin = new CheckBox
        {
            Content = "Windows 로그인 시 Codex SyncBar 자동 시작",
            IsChecked = await LaunchAtLoginService.IsEnabledAsync(),
        };
        launchAtLogin.Click += async (_, _) =>
        {
            try
            {
                await LaunchAtLoginService.SetEnabledAsync(launchAtLogin.IsChecked == true);
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

        var weeklyMessageHeader = new TextBlock
        {
            Text = "주간 초기화 후 자동 메시지",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        };
        content.Children.Add(weeklyMessageHeader);
        content.Children.Add(new TextBlock
        {
            Text = "선택한 계정의 주간 한도가 초기화되면 ‘확인’ 메시지를 한 번 보냅니다. 처음 켤 때 미사용 계정은 즉시 실행합니다. 트레이에서도 동작하며 소량의 사용량을 소비합니다. 모델: gpt-5.6-luna · low",
            Style = (Style)Resources["MutedText"],
            TextWrapping = TextWrapping.Wrap,
        });
        var weeklyStatusRefreshers = new List<Action>();
        foreach (var anchorAccount in _configuration.Accounts)
        {
            var anchorRow = new StackPanel { Spacing = 3 };
            var anchorCheck = new CheckBox
            {
                Content = anchorAccount.Alias,
                IsChecked = _weeklyAnchorState.Preferences.IsEnabled(anchorAccount.Id),
                IsEnabled = !anchorAccount.IsPending && !anchorAccount.NeedsLogin,
            };
            var anchorStatus = new TextBlock
            {
                Text = WeeklyAnchorStatus(anchorAccount.Id),
                Style = (Style)Resources["MutedText"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(28, 0, 0, 0),
            };
            anchorCheck.Click += async (_, _) =>
            {
                anchorCheck.IsEnabled = false;
                try { await Controller.SetWeeklyAnchorEnabledAsync(anchorAccount.Id, anchorCheck.IsChecked == true); }
                catch (Exception error) { SetBanner(error.Message, true); }
                finally
                {
                    _weeklyAnchorState = Controller.WeeklyAnchor.Load();
                    anchorCheck.IsChecked = _weeklyAnchorState.Preferences.IsEnabled(anchorAccount.Id);
                    anchorCheck.IsEnabled = true;
                    anchorStatus.Text = WeeklyAnchorStatus(anchorAccount.Id);
                }
            };
            anchorRow.Children.Add(anchorCheck);
            anchorRow.Children.Add(anchorStatus);

            var anchorNow = new Button
            {
                Content = "지금 메시지 보내기",
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
            weeklyStatusRefreshers.Add(() =>
            {
                anchorStatus.Text = WeeklyAnchorStatus(anchorAccount.Id);
                anchorNow.IsEnabled = !Controller.WeeklyAnchor.IsRunning(anchorAccount.Id)
                    && !anchorAccount.IsPending && !anchorAccount.NeedsLogin;
            });
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
                await RefreshAllUsageAsync();
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
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(combo, $"알림 영역 {slotIndex + 1}번 사용량 항목");
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
                         UsageDisplayItem.FiveHour,
                         UsageDisplayItem.CodexWeekly,
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
        var weeklyStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        dialog.Opened += (_, _) =>
        {
            if (ReferenceEquals(sender, WeeklyMessagesButton))
                weeklyMessageHeader.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
        };
        weeklyStatusTimer.Tick += (_, _) =>
        {
            _weeklyAnchorState = Controller.WeeklyAnchor.Load();
            foreach (var refresh in weeklyStatusRefreshers) refresh();
        };
        weeklyStatusTimer.Start();
        try { await dialog.ShowAsync(); }
        finally { weeklyStatusTimer.Stop(); }
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

    private void EditAliasButtonIsEnabled(bool enabled)
    {
        EditAliasButton.IsEnabled = enabled;
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        busy = busy || _controllerBusy;
        LoadingRing.IsActive = busy;
        var selectedAccount = GetSelectedAccount();
        var selectedHasCredentials = selectedAccount is not null
            && !selectedAccount.IsPending
            && !selectedAccount.NeedsLogin
            && _authStore.ProfileArtifactExists(selectedAccount.Id);
        var canMutate = !busy && !_configurationRecoveryNeeded;
        RefreshButton.IsEnabled = !busy;
        WslSettingsButton.IsEnabled = canMutate;
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
        BannerText.Foreground = (Brush)Application.Current.Resources[
            isError ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
        NotifyTrayStateChanged();
    }

    private void NotifyTrayStateChanged() =>
        TrayStateChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class DeviceRow
{
    public DeviceRow(DeviceStatus status, SshDeviceConfiguration? configured, bool? wslEnabled = null)
    {
        Id = status.Id;
        DisplayName = status.DisplayName;
        Detail = status.Detail ?? (status.Id == "windows" ? "이 장치" : status.Id.StartsWith("wsl:") ? "WSL 배포판" : "SSH 장치");
        IsReachable = status.IsReachable;
        ProfileId = status.ProfileId;
        IsConfigured = configured is not null || wslEnabled.HasValue;
        IsEnabled = wslEnabled ?? configured?.Enabled == true;
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

    public Brush StateBrush => (Brush)Application.Current.Resources[
        !IsEnabled && IsConfigured ? "TextFillColorSecondaryBrush"
            : IsReachable ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush"];
}

public sealed class TokenUsageRow
{
    public TokenUsageRow(DeviceTokenUsage usage, AccountProfile? profile)
    {
        DisplayName = usage.DisplayName;
        AccountText = usage.Error is not null
            ? "연결 또는 세션 수집 실패"
            : profile?.Alias ?? (usage.IsReachable ? "적용 계정 확인 중" : "연결 안 됨");
        AccountBrush = (Brush)Application.Current.Resources[
            usage.Error is not null || !usage.IsReachable ? "SystemFillColorCriticalBrush"
                : profile is null ? "TextFillColorSecondaryBrush" : "SystemFillColorSuccessBrush"];

        if (usage.Summary is { } summary)
        {
            UsageText = $"{TokenUsageFormatting.Tokens(summary.TotalTokens)} 토큰 · 요청 {summary.Requests:N0}회";
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
