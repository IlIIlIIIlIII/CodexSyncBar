using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Windows.Core.Tests;

public sealed class AuthVaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-vault-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsPaths _paths;
    private readonly AuthStore _store;
    public AuthVaultTests()
    {
        _paths = new WindowsPaths(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
        _paths.EnsureDirectories();
        _store = new AuthStore(_paths);
    }

    private string Source()
    {
        var source = Path.Combine(_paths.LoginSessionsDirectory, "incoming.json");
        WindowsPathSafety.WritePrivateBytes(source, TestAuth.Bytes(
            TestAuth.Create("test-account", "test-access", "test-refresh", expiresAt: 1_790_000_000_000)));
        return source;
    }

    [Fact]
    public void ExplicitTestHomeCannotBeRedirectedByParentClaudeEnvironment()
    {
        Assert.Equal(Path.Combine(_root, "home", ".claude"), _paths.ClaudeHome);
        Assert.StartsWith(_root + Path.DirectorySeparatorChar, _paths.StateRoot, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginChildFilesInheritPrivateDirectoryPermissions()
    {
        var child = Path.Combine(_paths.LoginSessionsDirectory, "child-created-auth.json");
        File.WriteAllText(child, "{}");
        if (OperatingSystem.IsWindows())
        {
            var security = new FileInfo(child).GetAccessControl();
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            foreach (System.Security.AccessControl.FileSystemAccessRule rule in security.GetAccessRules(
                true, true, typeof(System.Security.Principal.SecurityIdentifier)))
            {
                if (rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow)
                    Assert.Equal(identity.User, rule.IdentityReference);
            }
        }
        else
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(_paths.LoginSessionsDirectory));
    }

    [Fact]
    public void ImportMaterializesCompatibleActiveAndAccessOnlyCredentials()
    {
        _store.ImportAuth(Source(), 1);
        _store.SwitchActive(1);
        Assert.Equal("test-refresh", _store.ReadCredentials(1).RefreshToken);
        Assert.Equal("test-account", _store.ReadActiveAccountId());
        Assert.Empty(_store.CreateAccessOnlyCopy(1).RefreshToken!);
        Assert.Contains("test-refresh", File.ReadAllText(_paths.ActiveAuthFile));
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("test-refresh", File.ReadAllText(_paths.ProfileAuthFile(1)));
            Assert.Contains("protectedAuth", File.ReadAllText(_paths.ProfileAuthFile(1)));
            WindowsPathSafety.EnsurePrivateFile(_paths.ActiveAuthFile, "test", 16384);
        }
        else
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_paths.ActiveAuthFile));
    }

    [Fact]
    public void ActiveAuthMayHaveOtherReadersButNotOtherWriters()
    {
        _store.ImportAuth(Source(), 1);
        _store.SwitchActive(1);
        // Claude Code rewrites its login with its folder's permissions, and other
        // tools add readers there: the Codex sandbox gives its users read access.
        AllowOthers(_paths.ActiveAuthFile, write: false);
        Assert.Equal("test-account", _store.ReadActiveAccountId());
        // A switch writes the login private again.
        _store.SwitchActive(1);
        if (OperatingSystem.IsWindows()) WindowsPathSafety.EnsurePrivateFile(_paths.ActiveAuthFile, "test", 16384);
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_paths.ActiveAuthFile));

        AllowOthers(_paths.ActiveAuthFile, write: true);
        Assert.Throws<ClaudeSyncBarException>(() => _store.ReadActiveAccountId());
        Assert.Throws<ClaudeSyncBarException>(() => _store.SwitchActive(1));
    }

    private static void AllowOthers(string path, bool write)
    {
        if (OperatingSystem.IsWindows())
        {
            var file = new FileInfo(path);
            var acl = file.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                write ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            file.SetAccessControl(acl);
        }
        else
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                | (write ? UnixFileMode.GroupWrite : 0));
    }

    [Fact]
    public void RollbackCopiesCanBeReadButStayProtectedOnWindows()
    {
        var source = Source();
        var backup = Path.Combine(_paths.LoginTransactionsDirectory, "backup.json");
        _store.CopyAuthFile(source, backup);
        Assert.Equal("test-refresh", _store.ReadAuthFile(backup).RefreshToken);
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain("test-refresh", File.ReadAllText(backup));
        _store.CopyAuthFile(backup, _paths.ActiveAuthFile);
        Assert.Equal("test-account", _store.ReadActiveAccountId());
    }

    [Fact]
    public void SwitchingReplacesOnlyTheLoginEntriesOfClaudeCodeFiles()
    {
        Directory.CreateDirectory(_paths.ClaudeHome);
        WindowsPathSafety.WritePrivateBytes(_paths.ActiveAuthFile,
            """{"mcpOAuth":{"fixture":{"accessToken":"mcp-token"}},"claudeAiOauth":{"accessToken":"stale"}}"""u8.ToArray());
        WindowsPathSafety.WritePrivateBytes(_paths.ClaudeConfigFile,
            """{"numStartups":7,"projects":{"/work":{"history":["한글 기록"]}},"oauthAccount":{"accountUuid":"stale"}}"""u8.ToArray());
        _store.ImportAuth(Source(), 1);
        _store.SwitchActive(1);

        using (var credentials = JsonDocument.Parse(File.ReadAllText(_paths.ActiveAuthFile)))
        {
            Assert.Equal("mcp-token", credentials.RootElement.GetProperty("mcpOAuth").GetProperty("fixture").GetProperty("accessToken").GetString());
            var oauth = credentials.RootElement.GetProperty("claudeAiOauth");
            Assert.Equal("test-access", oauth.GetProperty("accessToken").GetString());
            Assert.Equal(1_790_000_000_000, oauth.GetProperty("expiresAt").GetInt64());
            Assert.Equal("max", oauth.GetProperty("subscriptionType").GetString());
        }
        var config = File.ReadAllText(_paths.ClaudeConfigFile);
        Assert.Contains("한글 기록", config);
        using (var document = JsonDocument.Parse(config))
        {
            Assert.Equal(7, document.RootElement.GetProperty("numStartups").GetInt32());
            Assert.Equal("test-account", document.RootElement.GetProperty("oauthAccount").GetProperty("accountUuid").GetString());
        }
        Assert.False(Directory.Exists(_paths.ClaudeConfigFile + ".lock"));

        // Signing out removes the login and nothing else.
        _store.RestoreActive(null);
        Assert.Null(_store.ReadActiveAccountId());
        Assert.Null(_store.ReadActiveAuth());
        Assert.Contains("mcp-token", File.ReadAllText(_paths.ActiveAuthFile));
        Assert.DoesNotContain("claudeAiOauth", File.ReadAllText(_paths.ActiveAuthFile));
        Assert.Contains("numStartups", File.ReadAllText(_paths.ClaudeConfigFile));
        Assert.DoesNotContain("oauthAccount", File.ReadAllText(_paths.ClaudeConfigFile));
    }

    [Fact]
    public void UnreadableClaudeConfigIsNeverReplaced()
    {
        Directory.CreateDirectory(_paths.ClaudeHome);
        WindowsPathSafety.WritePrivateBytes(_paths.ClaudeConfigFile, "{ not json"u8.ToArray());
        _store.ImportAuth(Source(), 1);
        Assert.ThrowsAny<Exception>(() => _store.SwitchActive(1));
        Assert.Equal("{ not json", File.ReadAllText(_paths.ClaudeConfigFile));
    }

    [Fact]
    public void IdentityNamesTheOrganizationSeatAndPlanComesFromTheLogin()
    {
        var source = Path.Combine(_paths.LoginSessionsDirectory, "organization.json");
        WindowsPathSafety.WritePrivateBytes(source, TestAuth.Bytes(
            TestAuth.Create("person", "access", "refresh", email: "person@example.com", organization: "team")));
        _store.ImportAuth(source, 2);
        var credentials = _store.ReadCredentials(2);
        Assert.Equal("team:person", credentials.AccountId);
        Assert.Equal("person@example.com", credentials.Email);
        Assert.Equal("Max 20x", credentials.Plan);
        Assert.Null(credentials.ExpiresAt);

        // The same person in another organization is a separate login.
        var other = Path.Combine(_paths.LoginSessionsDirectory, "other-organization.json");
        WindowsPathSafety.WritePrivateBytes(other, TestAuth.Bytes(
            TestAuth.Create("person", "access-2", "refresh-2", organization: "other-team")));
        _store.ImportAuth(other, 3);
        Assert.Equal("other-team:person", _store.ReadCredentials(3).AccountId);
        Assert.Throws<ClaudeSyncBarException>(() => _store.ImportAuth(other, 4));
    }

    [Fact]
    public void TokensRotatedByARunningCliAreAdoptedOnlyWhenTheyExpireLater()
    {
        _store.ImportAuth(Source(), 1);
        _store.SwitchActive(1);
        void WriteActive(string access, long expiresAt)
        {
            var source = Path.Combine(_paths.LoginSessionsDirectory, "rotated-" + Guid.NewGuid().ToString("N") + ".json");
            WindowsPathSafety.WritePrivateBytes(source, TestAuth.Bytes(
                TestAuth.Create("test-account", access, "refresh-" + access, expiresAt: expiresAt)));
            _store.CopyAuthFile(source, _paths.ActiveAuthFile);
        }

        WriteActive("older", 1_780_000_000_000);
        _store.ReconcileActiveCredentials(1);
        Assert.Equal("test-access", _store.ReadCredentials(1).AccessToken);

        WriteActive("newer", 1_800_000_000_000);
        _store.ReconcileActiveCredentials(1);
        Assert.Equal("newer", _store.ReadCredentials(1).AccessToken);
        Assert.Equal("refresh-newer", _store.ReadCredentials(1).RefreshToken);
    }

    private void ImportSecond()
    {
        var source = Path.Combine(_paths.LoginSessionsDirectory, "second.json");
        WindowsPathSafety.WritePrivateBytes(source, TestAuth.Bytes(
            TestAuth.Create("second-account", "second-access", "second-refresh", expiresAt: 1_790_000_000_000)));
        _store.ImportAuth(source, 2);
    }

    // What another Claude session on the machine does to the files both share.
    private void RewriteSharedFiles(string? accessToken = null, string? account = null)
    {
        if (accessToken is not null)
            File.WriteAllText(_paths.ActiveAuthFile, "{\"claudeAiOauth\":{\"accessToken\":\"" + accessToken
                + "\",\"refreshToken\":\"refresh-" + accessToken + "\",\"expiresAt\":1900000000000}}");
        if (account is not null)
            File.WriteAllText(_paths.ClaudeConfigFile, "{\"oauthAccount\":{\"accountUuid\":\"" + account + "\"}}");
    }

    [Fact]
    public void AnotherSessionsAccountInTheSharedConfigCannotChangeWhoseTokensTheseAre()
    {
        _store.ImportAuth(Source(), 1);
        ImportSecond();
        _store.SwitchActive(1);

        // A session hosted elsewhere wrote its own account into the shared config.
        RewriteSharedFiles(account: "second-account");
        Assert.Equal("test-account", _store.ReadActiveAccountId());
        Assert.True(_store.ReadCredentials(1).IsActive);
        Assert.False(_store.ReadCredentials(2).IsActive);
        _store.ReconcileActiveCredentials(2);
        Assert.Equal("second-refresh", _store.ReadCredentials(2).RefreshToken);

        // The CLI refreshed its tokens while the config still named its account...
        RewriteSharedFiles(accessToken: "rotated", account: "test-account");
        Assert.Equal("test-account", _store.ReadActiveAccountId());
        // ...so a later rewrite of the config still cannot move them to another account.
        RewriteSharedFiles(account: "second-account");
        Assert.Equal("test-account", _store.ReadActiveAccountId());
        _store.ReconcileActiveCredentials(2);
        Assert.Equal("second-refresh", _store.ReadCredentials(2).RefreshToken);
        _store.ReconcileActiveCredentials(1);
        Assert.Equal("refresh-rotated", _store.ReadCredentials(1).RefreshToken);
    }

    [Fact]
    public async Task UnattributableTokensStayUnknownUntilTheServiceNamesTheirAccount()
    {
        _store.ImportAuth(Source(), 1);
        ImportSecond();
        _store.SwitchActive(1);

        // New tokens and a different account at once: a manual sign-in, or a refresh plus a rewrite.
        RewriteSharedFiles(accessToken: "changed", account: "second-account");
        Assert.Null(_store.ReadActiveAccountId());
        _store.ReconcileActiveCredentials(1);
        _store.ReconcileActiveCredentials(2);
        Assert.Equal("test-refresh", _store.ReadCredentials(1).RefreshToken);
        Assert.Equal("second-refresh", _store.ReadCredentials(2).RefreshToken);

        // A rejected token names nobody.
        await _store.VerifyActiveOwnerAsync((_, _) => Task.FromResult<ClaudeOauthAccount?>(null));
        Assert.Null(_store.ReadActiveAccountId());

        string? asked = null;
        await _store.VerifyActiveOwnerAsync((token, _) =>
        {
            asked = token;
            return Task.FromResult<ClaudeOauthAccount?>(new() { AccountUuid = "test-account" });
        });
        Assert.Equal("changed", asked);
        Assert.Equal("test-account", _store.ReadActiveAccountId());
        _store.ReconcileActiveCredentials(1);
        Assert.Equal("refresh-changed", _store.ReadCredentials(1).RefreshToken);
        Assert.Equal("second-refresh", _store.ReadCredentials(2).RefreshToken);

        // Once the owner is known, the service is not asked again.
        await _store.VerifyActiveOwnerAsync((_, _) => throw new InvalidOperationException("must not be asked"));
    }

    [Fact]
    public void LoginThatWasNeverInstalledFromHereIsShownButNeverStored()
    {
        _store.ImportAuth(Source(), 1);
        Directory.CreateDirectory(_paths.ClaudeHome);
        RewriteSharedFiles(accessToken: "manual", account: "test-account");
        Assert.Equal("test-account", _store.ReadActiveAccountId());
        _store.ReconcileActiveCredentials(1);
        Assert.Equal("test-refresh", _store.ReadCredentials(1).RefreshToken);
        // Backing up and restoring the login as found still works.
        Assert.Equal("refresh-manual", _store.ReadActiveAuth()!.RefreshToken);
    }

    [Fact]
    public void IsolatedCliSessionRoundTripsThroughClaudeCodeFiles()
    {
        var session = Path.Combine(_paths.LoginSessionsDirectory, "profile-1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        _store.CopyAuthFile(Source(), Path.Combine(session, "auth.json"));
        _store.MaterializeClaudeHome(session);
        Assert.Contains("test-refresh", File.ReadAllText(Path.Combine(session, ".credentials.json")));
        Assert.Contains("test-account", File.ReadAllText(Path.Combine(session, ".claude.json")));

        // The CLI rotated the tokens; its own file is then the copy to keep.
        File.WriteAllText(Path.Combine(session, ".credentials.json"),
            """{"claudeAiOauth":{"accessToken":"cli-access","refreshToken":"cli-refresh","expiresAt":1800000000000,"scopes":["user:inference"]}}""");
        var captured = _store.CaptureClaudeHome(session);
        Assert.Equal("cli-refresh", captured.RefreshToken);
        Assert.Equal("test-account", captured.AccountId);
        Assert.False(File.Exists(Path.Combine(session, ".credentials.json")));
        Assert.Equal("cli-access", _store.ReadAuthFile(Path.Combine(session, "auth.json")).AccessToken);

        // Without any account record the tokens cannot be attributed.
        File.Delete(Path.Combine(session, ".claude.json"));
        File.WriteAllText(Path.Combine(session, ".credentials.json"),
            """{"claudeAiOauth":{"accessToken":"orphan","refreshToken":"orphan-refresh"}}""");
        Assert.Throws<AuthenticationRequiredException>(() => _store.CaptureClaudeHome(session));
        Assert.Equal("orphan-refresh",
            _store.CaptureClaudeHome(session, captured.Account).RefreshToken);
    }

    [Fact]
    public void PricingUsesClaudeRateCardsAndLeavesUnknownModelsUnpriced()
    {
        var usage = new ModelTokenUsage
        {
            Model = "claude-opus-5-5", InputTokens = 1_000_000, CachedInputTokens = 200_000,
            CacheWriteInputTokens = 100_000, CacheWriteOneHourInputTokens = 40_000, OutputTokens = 100_000,
            ServiceTier = "fast",
        };
        // 700K uncached, 60K five-minute and 40K one-hour cache writes, 200K cache reads, doubled by fast mode.
        Assert.Equal(10.92m, TokenUsagePricing.EstimateUsd(usage).PricedUsd);
        Assert.Equal(5.46m, TokenUsagePricing.EstimateUsd(new ModelTokenUsage
        {
            Model = usage.Model, InputTokens = usage.InputTokens, CachedInputTokens = usage.CachedInputTokens,
            CacheWriteInputTokens = usage.CacheWriteInputTokens, CacheWriteOneHourInputTokens = usage.CacheWriteOneHourInputTokens,
            OutputTokens = usage.OutputTokens, ServiceTier = "priority",
        }).PricedUsd);

        ModelTokenUsage Haiku(bool longContext) => new()
        {
            Model = "claude-haiku-5-5", InputTokens = 200_000, CachedInputTokens = 100_000,
            OutputTokens = 10_000, IsLongContext = longContext,
        };
        Assert.Equal(0.016m, TokenUsagePricing.EstimateUsd(Haiku(false)).PricedUsd);
        Assert.Equal(0.08m, TokenUsagePricing.EstimateUsd(Haiku(true)).PricedUsd);
        Assert.Equal("Claude Fable 5.1", TokenUsagePricing.EstimateUsd(new ModelTokenUsage { Model = "claude-fable-5-1" }).CanonicalModel);
        Assert.Equal("Claude Opus 5", TokenUsagePricing.EstimateUsd(new ModelTokenUsage { Model = "claude-opus-5" }).CanonicalModel);
        Assert.False(TokenUsagePricing.EstimateUsd(new ModelTokenUsage { Model = "claude-opus-4-5" }).IsPriced);
        Assert.False(TokenUsagePricing.EstimateUsd(new ModelTokenUsage { Model = "gpt-5.4" }).IsPriced);
    }

    [Fact]
    public void ChangedWeeklyAnchorConfigurationBypassesBackoffOnlyOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new WeeklyAnchorRecord { LastAttemptAt = now, LastError = "model unsupported",
            LastAttemptConfigurationId = "previous" };
        var quota = new UsageWindow(0, null, null);
        Assert.Equal(WeeklyAnchorDecision.Trigger, WeeklyAnchorDecisionEngine.Decide(true, quota, record, now));
        record.LastAttemptConfigurationId = WeeklyAnchorConfiguration.Id;
        Assert.Equal(WeeklyAnchorDecision.None, WeeklyAnchorDecisionEngine.Decide(true, quota, record, now));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
