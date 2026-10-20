using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Windows.Core.Tests;

/// <summary>
/// Drives the login service against a stand-in for the Claude Code CLI. The
/// stand-in follows the CLI's contract: it hands the sign-in page to $BROWSER
/// and stores the login in $CLAUDE_CONFIG_DIR.
/// </summary>
public sealed class LoginProtocolTests
{
    private const string LoginUrl = "https://claude.ai/oauth/authorize?code=true&state=fixture";

    private static string Cli(string account, string email = "new@example.com") => $$$"""
        #!/bin/bash
        [ "$1 $2 $3" = "auth login --claudeai" ] || exit 64
        env >"$FIXTURE_ENVIRONMENT"
        "$BROWSER" "{{{LoginUrl}}}" || exit 65
        # The real CLI waits here for the browser to finish signing in.
        for attempt in $(seq 200); do [ -e "$FIXTURE_OPENED" ] && break; sleep 0.05; done
        [ -e "$FIXTURE_OPENED" ] || exit 66
        printf '%s' '{"claudeAiOauth":{"accessToken":"login-access","refreshToken":"login-refresh","expiresAt":1800000000000,"scopes":["user:inference","user:profile"],"subscriptionType":"pro"}}' >"$CLAUDE_CONFIG_DIR/.credentials.json"
        printf '%s' '{"hasCompletedOnboarding":true,"oauthAccount":{"accountUuid":"{{{account}}}","emailAddress":"{{{email}}}","organizationUuid":"org-1"}}' >"$CLAUDE_CONFIG_DIR/.claude.json"
        chmod 600 "$CLAUDE_CONFIG_DIR/.credentials.json" "$CLAUDE_CONFIG_DIR/.claude.json"
        echo 'Login successful.'

        """;

    [Fact]
    public async Task LoginOpensTheSignInPageForThatAccountAndImportsTheStoredLogin()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture(Cli("account-1"));
        var progress = new List<string>();
        await fixture.Service.LoginAsync(1, replaceExisting: false, new SynchronousProgress(progress.Add));

        Assert.Equal((1, new Uri(LoginUrl)), Assert.Single(fixture.Opened));
        var credentials = fixture.Auth.ReadCredentials(1);
        Assert.Equal("org-1:account-1", credentials.AccountId);
        Assert.Equal("new@example.com", credentials.Email);
        Assert.Equal("login-refresh", credentials.RefreshToken);
        Assert.Equal("Pro", credentials.Plan);
        Assert.DoesNotContain("login-refresh", File.ReadAllText(fixture.Paths.ProfileAuthFile(1)));
        // Logging in registers an account; it never changes the active login.
        Assert.Null(fixture.Auth.ReadActiveAccountId());
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Paths.LoginSessionsDirectory));
        Assert.Contains(progress, message => message.Contains("Chrome", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CliRunsOnlyAgainstItsOwnSessionDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture(Cli("account-1"));
        var inherited = new[] { "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL", "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CONFIG_DIR" };
        var original = inherited.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in inherited) Environment.SetEnvironmentVariable(name, "inherited-" + name);
            await fixture.Service.LoginAsync(1, replaceExisting: false);
        }
        finally
        {
            foreach (var (name, value) in original) Environment.SetEnvironmentVariable(name, value);
        }

        var environment = File.ReadAllLines(fixture.EnvironmentFile);
        Assert.DoesNotContain(environment, line => line.Contains("inherited-", StringComparison.Ordinal)
            && !line.StartsWith("CLAUDE_CONFIG_DIR=", StringComparison.Ordinal));
        var configDirectory = Assert.Single(environment, line => line.StartsWith("CLAUDE_CONFIG_DIR=", StringComparison.Ordinal))["CLAUDE_CONFIG_DIR=".Length..];
        Assert.StartsWith(fixture.Paths.LoginSessionsDirectory + "/profile-1-", configDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedCliLoginReportsItsMessageAndStoresNothing()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture("""
            #!/bin/bash
            echo 'Opening browser to sign in'
            echo 'Login failed: fixture reason' >&2
            exit 1

            """);
        var error = await Assert.ThrowsAsync<ClaudeSyncBarException>(() => fixture.Service.LoginAsync(1, replaceExisting: false));
        Assert.Contains("fixture reason", error.Message);
        Assert.False(fixture.Auth.ProfileArtifactExists(1));
        Assert.Empty(fixture.Opened);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Paths.LoginSessionsDirectory));
    }

    [Fact]
    public async Task SignInPageOutsideClaudeIsRefusedBeforeAnyBrowserStarts()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture("""
            #!/bin/bash
            "$BROWSER" "https://claude.ai.example.invalid/oauth/authorize"
            sleep 30

            """, useRealBrowserCheck: true);
        var error = await Assert.ThrowsAsync<ClaudeSyncBarException>(() => fixture.Service.LoginAsync(1, replaceExisting: false));
        Assert.Contains("허용된 Claude 주소", error.Message);
        Assert.False(fixture.Auth.ProfileArtifactExists(1));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Paths.LoginSessionsDirectory));
    }

    [Fact]
    public async Task ReloginWithAnotherAccountNeedsExplicitReplacement()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture(Cli("account-1"));
        await fixture.Service.LoginAsync(1, replaceExisting: false);

        using var other = new Fixture(Cli("account-2", "other@example.com"), fixture);
        await Assert.ThrowsAsync<ClaudeSyncBarException>(() => other.Service.LoginAsync(1, replaceExisting: false));
        Assert.Equal("org-1:account-1", fixture.Auth.ReadCredentials(1).AccountId);
        await other.Service.LoginAsync(1, replaceExisting: true);
        Assert.Equal("org-1:account-2", fixture.Auth.ReadCredentials(1).AccountId);
        Assert.Equal("other@example.com", fixture.Auth.ReadCredentials(1).Email);
    }

    [Fact]
    public async Task CancellingLoginStopsTheCliAndRemovesItsSession()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture("""
            #!/bin/bash
            "$BROWSER" "https://claude.ai/oauth/authorize?pending=1"
            sleep 60

            """);
        using var cancellation = new CancellationTokenSource();
        var login = fixture.Service.LoginAsync(1, replaceExisting: false, cancellationToken: cancellation.Token);
        for (var attempt = 0; attempt < 200 && fixture.Opened.Count == 0; attempt++) await Task.Delay(50);
        Assert.Single(fixture.Opened);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        Assert.False(fixture.Auth.ProfileArtifactExists(1));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Paths.LoginSessionsDirectory));
    }

    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly bool _ownsRoot;
        public WindowsPaths Paths { get; }
        public AuthStore Auth { get; }
        public ClaudeLoginService Service { get; }
        public List<(int ProfileId, Uri Url)> Opened { get; } = [];
        public string EnvironmentFile => Path.Combine(_root, "cli-environment");
        private string OpenedFile => Path.Combine(_root, "browser-opened-" + GetHashCode());

        public Fixture(string cliScript, Fixture? shared = null, bool useRealBrowserCheck = false)
        {
            _ownsRoot = shared is null;
            _root = shared?._root ?? Path.Combine(Path.GetTempPath(), "syncbar-login-" + Guid.NewGuid().ToString("N"));
            Paths = shared?.Paths ?? new(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
            Paths.EnsureDirectories();
            Auth = shared?.Auth ?? new(Paths);
            var cli = Path.Combine(_root, "claude-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(cli, cliScript.Replace("$FIXTURE_ENVIRONMENT", EnvironmentFile, StringComparison.Ordinal)
                .Replace("$FIXTURE_OPENED", OpenedFile, StringComparison.Ordinal));
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Service = new ClaudeLoginService(Paths, Auth, new BrowserLoginService(Paths))
            {
                ExecutableOverride = cli,
                OpenUrlOverride = useRealBrowserCheck ? null : (profileId, url) =>
                {
                    lock (Opened) Opened.Add((profileId, url));
                    File.WriteAllText(OpenedFile, "opened");
                },
            };
        }

        public void Dispose()
        {
            if (_ownsRoot && Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
