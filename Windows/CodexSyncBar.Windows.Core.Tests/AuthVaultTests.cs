using System.Text.Json;
using System.Security.AccessControl;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

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
        WindowsPathSafety.WritePrivateBytes(source, JsonSerializer.SerializeToUtf8Bytes(new CodexAuthFile
        {
            AuthMode = "chatgpt", LastRefresh = "2026-09-19T00:00:00Z",
            Tokens = new CodexTokens { AccountId = "test-account", AccessToken = "test-access",
                RefreshToken = "test-refresh", IdToken = "test-id" },
        }));
        return source;
    }

    [Fact]
    public void ExplicitTestHomeCannotBeRedirectedByParentCodexEnvironment()
    {
        Assert.Equal(Path.Combine(_root, "home", ".codex"), _paths.CodexHome);
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
        Assert.Empty(_store.CreateAccessOnlyCopy(1).Tokens.RefreshToken!);
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
    public void RollbackCopiesCanBeReadButStayProtectedOnWindows()
    {
        var source = Source();
        var backup = Path.Combine(_paths.LoginTransactionsDirectory, "backup.json");
        _store.CopyAuthFile(source, backup);
        Assert.Equal("test-refresh", _store.ReadAuthFile(backup).Tokens.RefreshToken);
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain("test-refresh", File.ReadAllText(backup));
        _store.CopyAuthFile(backup, _paths.ActiveAuthFile);
        Assert.Equal("test-account", _store.ReadActiveAccountId());
    }

    [Fact]
    public void PricingMatchesMainLongContextAndUnknownModelRules()
    {
        var estimate = TokenUsagePricing.EstimateUsd(new ModelTokenUsage
        {
            Model = "gpt-6-astra", InputTokens = 1_000_000, CachedInputTokens = 200_000,
            CacheWriteInputTokens = 100_000, OutputTokens = 100_000, IsLongContext = true,
            ServiceTier = "priority",
        });
        Assert.Equal(48.8m, estimate.PricedUsd);
        Assert.False(TokenUsagePricing.EstimateUsd(new ModelTokenUsage { Model = "gpt-5.4-pro" }).IsPriced);
        Assert.False(TokenUsagePricing.EstimateUsd(new ModelTokenUsage { Model = "gpt-5.6-mystery" }).IsPriced);
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
