namespace CodexSyncBar.Windows.Core;

public sealed class WindowsPaths
{
    public WindowsPaths(string? home = null, string? localAppData = null)
    {
        Home = Path.GetFullPath(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        LocalAppData = Path.GetFullPath(
            localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        var overrideRoot = Environment.GetEnvironmentVariable("CODEX_SYNCBAR_STATE_ROOT");
        var legacyRoot = Path.Combine(Home, ".local", "share", "gpt-switch");
        StateRoot = !string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.GetFullPath(overrideRoot)
            : File.Exists(Path.Combine(legacyRoot, "config.json"))
                ? legacyRoot
                : Path.Combine(LocalAppData, "CodexSyncBar");

        var configuredCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        CodexHome = !string.IsNullOrWhiteSpace(configuredCodexHome)
            ? Path.GetFullPath(configuredCodexHome)
            : Path.Combine(Home, ".codex");
    }

    public string Home { get; }

    public string LocalAppData { get; }

    public string StateRoot { get; }

    public string ProfilesDirectory => Path.Combine(StateRoot, "profiles");

    public string ConfigurationFile => Path.Combine(StateRoot, "config.json");

    public string CodexHome { get; }

    public string ActiveAuthFile => Path.Combine(CodexHome, "auth.json");

    public string AppDataDirectory => Path.Combine(LocalAppData, "CodexSyncBar");

    public string ChromeProfilesDirectory => Path.Combine(AppDataDirectory, "ChromeProfiles");

    public string BrowserCleanupFile => Path.Combine(StateRoot, "browser-cleanup.json");

    // Packaged WinUI apps virtualize writes below LocalAppData. External
    // command-line children such as codex.cmd do not see that virtualized
    // location, so their temporary CODEX_HOME must live outside it.
    public string ExternalRuntimeDirectory => Path.Combine(Home, ".codex-syncbar");

    public string LoginSessionsDirectory => Path.Combine(ExternalRuntimeDirectory, "LoginSessions");

    public string CursorBridgeDirectory => Path.Combine(AppDataDirectory, "Cursor");

    public string CursorBridgePreferencesFile => Path.Combine(StateRoot, "cursor-bridge.json");

    public string CursorActivationFile => Path.Combine(StateRoot, "cursor-codex-activation.json");

    public string CursorTransactionFile => Path.Combine(StateRoot, "cursor-codex-transaction.json");

    public string CursorTransactionLockFile => Path.Combine(StateRoot, "cursor-codex.lock");

    public string LogoutTransactionsDirectory => Path.Combine(StateRoot, "logout-transactions");

    public string LoginTransactionsDirectory => Path.Combine(StateRoot, "login-transactions");

    public string DeviceActivationTransactionsDirectory => Path.Combine(StateRoot, "device-activation-transactions");

    public string SecretCleanupTransactionsDirectory => Path.Combine(StateRoot, "secret-cleanup-transactions");

    public string RemoteBootstrapTransactionsDirectory => Path.Combine(StateRoot, "remote-bootstrap-transactions");

    public string ControllerLockFile => Path.Combine(StateRoot, ".controller-lock");

    public string CursorModelCatalogFile => Path.Combine(StateRoot, "cursor-codex-model-catalog.json");

    public string CursorModelCatalogBackupFile => Path.Combine(StateRoot, "cursor-codex-model-catalog.backup");

    public string UsageCacheFile => Path.Combine(StateRoot, "usage-cache.json");

    public string UsageDisplayPreferencesFile => Path.Combine(StateRoot, "usage-display.json");

    public string MenuBarUsagePreferencesFile => Path.Combine(StateRoot, "menu-bar-usage.json");

    public string SelectedProfileFile => Path.Combine(StateRoot, "selected-profile.json");

    public string WeeklyAnchorFile => Path.Combine(StateRoot, "weekly-anchor.json");

    public string AuthMaintenanceStateFile => Path.Combine(StateRoot, "auth-maintenance.json");

    public string SshAskPassFile => Path.Combine(CursorBridgeDirectory, "ssh-askpass.cmd");

    public string RuntimeDirectory => Path.Combine(AppContext.BaseDirectory, "Runtime");

    public string BundledGptSwitch => Path.Combine(RuntimeDirectory, "gpt-switch");

    public string BundledAskPass => Path.Combine(RuntimeDirectory, "codex-syncbar-askpass");

    public string BundledUsageSummary => Path.Combine(RuntimeDirectory, "usage-summary.mjs");

    public string BundledCursorBridge => Path.Combine(RuntimeDirectory, "cursor-codex-bridge.mjs");

    public string BundledCursorRemoteManager => Path.Combine(RuntimeDirectory, "cursor-remote-manager.mjs");

    public string BundledPdfExtractor => Path.Combine(RuntimeDirectory, "PdfExtractor", "cursor-file-extractor.exe");

    public string ProfileAuthFile(int profileId) =>
        Path.Combine(ProfilesDirectory, $"{profileId}.auth.json");

    public string ChromeProfileDirectory(int profileId) =>
        Path.Combine(ChromeProfilesDirectory, $"profile-{profileId}");

    public void EnsureDirectories()
    {
        WindowsPathSafety.EnsureDirectory(Home, "Windows 사용자 홈 디렉터리");
        WindowsPathSafety.EnsureDirectory(LocalAppData, "Windows LocalAppData 디렉터리");
        WindowsPathSafety.EnsureDirectory(StateRoot, "SyncBar 상태 디렉터리");
        WindowsPathSafety.EnsureDirectory(ProfilesDirectory, "SyncBar 프로필 디렉터리");
        WindowsPathSafety.EnsureDirectory(CodexHome, "Codex 홈 디렉터리");
        WindowsPathSafety.EnsureDirectory(AppDataDirectory, "SyncBar 로컬 데이터 디렉터리");
        WindowsPathSafety.EnsureDirectory(ChromeProfilesDirectory, "Chrome 프로필 디렉터리");
        WindowsPathSafety.EnsureDirectory(ExternalRuntimeDirectory, "SyncBar 외부 런타임 디렉터리");
        WindowsPathSafety.EnsureDirectory(LoginSessionsDirectory, "로그인 세션 디렉터리");
        WindowsPathSafety.EnsureDirectory(LoginTransactionsDirectory, "로그인 복구 디렉터리");
        WindowsPathSafety.EnsureDirectory(RemoteBootstrapTransactionsDirectory, "원격 부트스트랩 복구 디렉터리");
        WindowsPathSafety.EnsureDirectory(CursorBridgeDirectory, "Cursor 브리지 디렉터리");
    }
}
