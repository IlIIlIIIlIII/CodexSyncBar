namespace ClaudeSyncBar.Windows.Core;

/// <summary>Where Claude Code, Claude Desktop and this app keep their state on Windows.</summary>
public sealed class WindowsPaths
{
    public WindowsPaths(string? home = null, string? localAppData = null, string? runtimeDirectory = null)
    {
        IsIsolated = home is not null || localAppData is not null || runtimeDirectory is not null;
        Home = Path.GetFullPath(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        LocalAppData = Path.GetFullPath(localAppData ?? (home is null
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.Combine(Home, "AppData", "Local")));

        // Explicit homes are used by isolated tests and import previews. Never let
        // the parent Claude process redirect them to the real user's login.
        var useEnvironment = home is null && localAppData is null;
        var overrideRoot = useEnvironment ? Environment.GetEnvironmentVariable("CLAUDE_SYNCBAR_STATE_ROOT") : null;
        StateRoot = !string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.GetFullPath(overrideRoot)
            : Path.Combine(LocalAppData, "claude-syncbar");

        // Claude Code keeps its global config next to the credentials when
        // CLAUDE_CONFIG_DIR is set, and in the home directory otherwise.
        var configuredClaudeHome = useEnvironment ? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") : null;
        var hasConfiguredHome = !string.IsNullOrWhiteSpace(configuredClaudeHome) && Path.IsPathFullyQualified(configuredClaudeHome);
        ClaudeHome = hasConfiguredHome ? Path.GetFullPath(configuredClaudeHome!) : Path.Combine(Home, ".claude");
        ClaudeConfigFile = Path.Combine(hasConfiguredHome ? ClaudeHome : Home, ".claude.json");
        RuntimeDirectory = Path.GetFullPath(runtimeDirectory ?? Path.Combine(AppContext.BaseDirectory, "Runtime"));
        RoamingAppData = home is null && useEnvironment
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(Home, "AppData", "Roaming");
    }

    private const string MovedNote = ".user-data-left.";

    public bool IsIsolated { get; }

    public string Home { get; }

    public string LocalAppData { get; }

    public string RoamingAppData { get; }

    public string StateRoot { get; }

    public string ProfilesDirectory => Path.Combine(StateRoot, "profiles");

    public string ConfigurationFile => Path.Combine(StateRoot, "config.json");

    public string ClaudeHome { get; }

    /// <summary>Claude Code's global config; holds the oauthAccount that names the signed-in account.</summary>
    public string ClaudeConfigFile { get; }

    /// <summary>Claude Code's credential store; its claudeAiOauth entry is the active login.</summary>
    public string ActiveAuthFile => Path.Combine(ClaudeHome, ".credentials.json");

    /// <summary>
    /// The profile Claude Desktop opens: its sign-in, settings and sessions. The
    /// app kept it in Roaming\Claude; the Store package moves it to
    /// LocalAppData\Claude-Data\&lt;package&gt; and leaves a ".user-data-left.&lt;package&gt;"
    /// note behind, so it is looked up each time rather than fixed at start.
    /// </summary>
    public string DesktopUserDataDirectory
    {
        get
        {
            var roaming = Path.Combine(RoamingAppData, "Claude");
            var moved = Path.Combine(LocalAppData, "Claude-Data");
            try
            {
                // The note where the data used to be names where it went.
                if (Directory.Exists(roaming))
                    foreach (var note in Directory.EnumerateFiles(roaming, MovedNote + "*"))
                    {
                        var name = Path.GetFileName(note)[MovedNote.Length..];
                        var target = Path.Combine(moved, name);
                        if (System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,64}$") && Directory.Exists(target)) return target;
                    }

                if (File.Exists(Path.Combine(roaming, "config.json"))) return roaming;
                // A new install can start in the moved place directly.
                if (Directory.Exists(moved))
                    return Directory.EnumerateDirectories(moved, "claude_*")
                        .Where(directory => File.Exists(Path.Combine(directory, "config.json")))
                        .OrderByDescending(directory => File.GetLastWriteTimeUtc(Path.Combine(directory, "config.json")))
                        .FirstOrDefault() ?? roaming;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

            return roaming;
        }
    }

    /// <summary>Claude Desktop sign-ins of the accounts that are not active, one cookie file per account.</summary>
    public string DesktopLoginsDirectory => Path.Combine(StateRoot, "desktop-logins");

    /// <summary>Kept for the shared contract; Windows never had a profile per account.</summary>
    public string DesktopProfilesDirectory => Path.Combine(StateRoot, "desktop-profiles");

    /// <summary>The Claude Code CLI that Claude Desktop downloads and keeps per version.</summary>
    public string DesktopBundledCliDirectory => Path.Combine(DesktopUserDataDirectory, "claude-code");

    public string AppDataDirectory => StateRoot;

    public string ChromeProfilesDirectory => Path.Combine(ExternalRuntimeDirectory, "ChromeProfiles");

    public string BrowserCleanupFile => Path.Combine(StateRoot, "browser-cleanup.json");

    // Isolated CLI sessions and account browser profiles live below the private state root.
    public string ExternalRuntimeDirectory => Path.Combine(StateRoot, "runtime");

    public string LoginSessionsDirectory => Path.Combine(ExternalRuntimeDirectory, "LoginSessions");

    public string LogoutTransactionsDirectory => Path.Combine(StateRoot, "logout-transactions");

    public string LoginTransactionsDirectory => Path.Combine(StateRoot, "login-transactions");

    public string DeviceActivationTransactionsDirectory => Path.Combine(StateRoot, "device-activation-transactions");

    public string SecretCleanupTransactionsDirectory => Path.Combine(StateRoot, "secret-cleanup-transactions");

    public string RemoteBootstrapTransactionsDirectory => Path.Combine(StateRoot, "remote-bootstrap-transactions");

    public string ControllerLockFile => Path.Combine(StateRoot, ".controller-lock");

    // Shared with the external usage helper.
    public string UsageCacheFile => Path.Combine(ExternalRuntimeDirectory, "usage-cache.json");

    public string UsageDisplayPreferencesFile => Path.Combine(StateRoot, "usage-display.json");

    public string MenuBarUsagePreferencesFile => Path.Combine(StateRoot, "menu-bar-usage.json");

    public string SelectedProfileFile => Path.Combine(StateRoot, "selected-profile.json");

    public string WeeklyAnchorFile => Path.Combine(StateRoot, "weekly-anchor.json");

    public string AuthMaintenanceStateFile => Path.Combine(StateRoot, "auth-maintenance.json");

    /// <summary>Which account the active Claude Code tokens were last known to belong to.</summary>
    public string ActiveLoginMarkerFile => Path.Combine(StateRoot, "active-login.json");

    /// <summary>The program ssh.exe runs for a password or key passphrase; it reads the secret from this user's store.</summary>
    public string BundledWindowsAskPass => Path.Combine(RuntimeDirectory, "ClaudeSyncBar.AskPass.exe");

    public string RuntimeDirectory { get; }

    public string BundledClaudeSwitch => Path.Combine(RuntimeDirectory, "claude-switch");

    public string BundledAskPass => Path.Combine(RuntimeDirectory, "claude-syncbar-askpass");

    public string BundledUsageSummary => Path.Combine(RuntimeDirectory, "usage-summary.mjs");

    public string ProfileAuthFile(int profileId) =>
        Path.Combine(ProfilesDirectory, $"{profileId}.auth.json");

    public string ChromeProfileDirectory(int profileId) =>
        Path.Combine(ChromeProfilesDirectory, $"profile-{profileId}");

    public void EnsureDirectories()
    {
        WindowsPathSafety.EnsureDirectory(Home, "Windows 사용자 홈 디렉터리");
        WindowsPathSafety.EnsureDirectory(LocalAppData, "Windows LocalAppData 디렉터리");
        WindowsPathSafety.EnsurePrivateDirectory(StateRoot, "SyncBar 상태 디렉터리");
        WindowsPathSafety.EnsurePrivateDirectory(ProfilesDirectory, "SyncBar 프로필 디렉터리");
        WindowsPathSafety.EnsureDirectory(ClaudeHome, "Claude 설정 디렉터리");
        WindowsPathSafety.EnsureOwnerWritableOnly(ClaudeHome, "Claude 설정 디렉터리");
        WindowsPathSafety.EnsurePrivateDirectory(ExternalRuntimeDirectory, "SyncBar 외부 런타임 디렉터리");
        WindowsPathSafety.EnsurePrivateDirectory(ChromeProfilesDirectory, "Chrome 프로필 디렉터리");
        WindowsPathSafety.EnsurePrivateDirectory(LoginSessionsDirectory, "로그인 세션 디렉터리");
        WindowsPathSafety.EnsurePrivateDirectory(LoginTransactionsDirectory, "로그인 복구 디렉터리");
        WindowsPathSafety.EnsurePrivateDirectory(RemoteBootstrapTransactionsDirectory, "원격 부트스트랩 복구 디렉터리");
    }
}
