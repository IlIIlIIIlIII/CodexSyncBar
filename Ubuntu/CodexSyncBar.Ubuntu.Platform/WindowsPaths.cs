namespace CodexSyncBar.Windows.Core;

public sealed class WindowsPaths
{
    public WindowsPaths(string? home = null, string? localAppData = null, string? runtimeDirectory = null)
    {
        IsIsolated = home is not null || localAppData is not null || runtimeDirectory is not null;
        Home = Path.GetFullPath(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        LocalAppData = Path.GetFullPath(
            localAppData ?? XdgPath("XDG_DATA_HOME", Path.Combine(Home, ".local", "share")));

        // Explicit homes are used by isolated tests/import previews. Never let
        // the parent Codex process redirect them to the real user's auth.
        var useEnvironment = home is null && localAppData is null;
        var overrideRoot = useEnvironment ? Environment.GetEnvironmentVariable("CODEX_SYNCBAR_STATE_ROOT") : null;
        StateRoot = !string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.GetFullPath(overrideRoot)
            : Path.Combine(LocalAppData, "codex-syncbar");

        var configuredCodexHome = useEnvironment ? Environment.GetEnvironmentVariable("CODEX_HOME") : null;
        CodexHome = !string.IsNullOrWhiteSpace(configuredCodexHome) && Path.IsPathFullyQualified(configuredCodexHome)
            ? Path.GetFullPath(configuredCodexHome)
            : Path.Combine(Home, ".codex");
        RuntimeDirectory = Path.GetFullPath(runtimeDirectory ?? Path.Combine(AppContext.BaseDirectory, "Runtime"));
    }

    public bool IsIsolated { get; }

    private static string XdgPath(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrEmpty(value) && Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : fallback;
    }

    public string Home { get; }

    public string LocalAppData { get; }

    public string StateRoot { get; }

    public string ProfilesDirectory => Path.Combine(StateRoot, "profiles");

    public string ConfigurationFile => Path.Combine(StateRoot, "config.json");

    public string CodexHome { get; }

    public string ActiveAuthFile => Path.Combine(CodexHome, "auth.json");

    public string AppDataDirectory => StateRoot;

    public string ChromeProfilesDirectory => Path.Combine(ExternalRuntimeDirectory, "ChromeProfiles");

    public string BrowserCleanupFile => Path.Combine(StateRoot, "browser-cleanup.json");

    // Isolated CLI sessions and account browser profiles live below the private XDG state root.
    public string ExternalRuntimeDirectory => Path.Combine(StateRoot, "runtime");

    public string LoginSessionsDirectory => Path.Combine(ExternalRuntimeDirectory, "LoginSessions");

    public string LogoutTransactionsDirectory => Path.Combine(StateRoot, "logout-transactions");

    public string LoginTransactionsDirectory => Path.Combine(StateRoot, "login-transactions");

    public string DeviceActivationTransactionsDirectory => Path.Combine(StateRoot, "device-activation-transactions");

    public string SecretCleanupTransactionsDirectory => Path.Combine(StateRoot, "secret-cleanup-transactions");

    public string RemoteBootstrapTransactionsDirectory => Path.Combine(StateRoot, "remote-bootstrap-transactions");

    public string ControllerLockFile => Path.Combine(StateRoot, ".controller-lock");

    // Share this disposable cache with the external usage helper.
    public string UsageCacheFile => Path.Combine(ExternalRuntimeDirectory, "usage-cache.json");

    public string UsageDisplayPreferencesFile => Path.Combine(StateRoot, "usage-display.json");

    public string MenuBarUsagePreferencesFile => Path.Combine(StateRoot, "menu-bar-usage.json");

    public string SelectedProfileFile => Path.Combine(StateRoot, "selected-profile.json");

    public string WeeklyAnchorFile => Path.Combine(StateRoot, "weekly-anchor.json");

    public string AuthMaintenanceStateFile => Path.Combine(StateRoot, "auth-maintenance.json");

    public string BundledWindowsAskPass => Path.Combine(RuntimeDirectory, "codex-syncbar-linux-askpass");

    public string RuntimeDirectory { get; }

    public string BundledGptSwitch => Path.Combine(RuntimeDirectory, "gpt-switch");

    public string BundledAskPass => Path.Combine(RuntimeDirectory, "codex-syncbar-askpass");

    public string BundledUsageSummary => Path.Combine(RuntimeDirectory, "usage-summary.mjs");

    public string ProfileAuthFile(int profileId) =>
        Path.Combine(ProfilesDirectory, $"{profileId}.auth.json");

    public string ChromeProfileDirectory(int profileId) =>
        Path.Combine(ChromeProfilesDirectory, $"profile-{profileId}");

    public void EnsureDirectories()
    {
        WindowsPathSafety.EnsureDirectory(Home, "사용자 홈 디렉터리");
        WindowsPathSafety.EnsureDirectory(LocalAppData, "XDG 데이터 디렉터리");
        WindowsPathSafety.EnsurePrivateDirectory(StateRoot, "SyncBar 상태 디렉터리");
        WindowsPathSafety.EnsurePrivateDirectory(ProfilesDirectory, "SyncBar 프로필 디렉터리");
        WindowsPathSafety.EnsureDirectory(CodexHome, "Codex 홈 디렉터리");
        WindowsPathSafety.EnsureDirectory(AppDataDirectory, "SyncBar 로컬 데이터 디렉터리");
        WindowsPathSafety.EnsurePrivateDirectory(ExternalRuntimeDirectory, "SyncBar 외부 런타임 디렉터리");
        WindowsPathSafety.EnsurePrivateDirectory(ChromeProfilesDirectory, "Chrome 프로필 디렉터리");
        WindowsPathSafety.EnsurePrivateDirectory(LoginSessionsDirectory, "로그인 세션 디렉터리");
        WindowsPathSafety.EnsurePrivateDirectory(LoginTransactionsDirectory, "로그인 복구 디렉터리");
        WindowsPathSafety.EnsurePrivateDirectory(RemoteBootstrapTransactionsDirectory, "원격 부트스트랩 복구 디렉터리");
    }
}
