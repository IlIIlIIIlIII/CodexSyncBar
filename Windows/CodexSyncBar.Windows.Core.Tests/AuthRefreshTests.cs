using System.Text;
using System.Text.Json;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class AuthRefreshTests
{
    [Fact]
    public async Task RotationSurvivesFailureAfterServerHasAlreadyIssuedNewRefreshToken()
    {
        using var fixture = new Fixture();
        var service = new CodexAuthMaintenanceService(fixture.Paths, fixture.Auth, runRefreshServer: (_, runtime, _) =>
        {
            WriteAuth(Path.Combine(runtime, "auth.json"), AuthFile("new"));
            throw new IOException("rate limits unavailable after rotation");
        });
        await Assert.ThrowsAsync<IOException>(() => service.RefreshAsync(1));
        Assert.Equal("refresh-new", fixture.Auth.ReadCredentials(1).RefreshToken);
        Assert.Equal("access-new", fixture.Auth.ReadActiveAuth()!.Tokens.AccessToken);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.LoginSessionsDirectory, "refresh-profile-*"));
    }

    [Fact]
    public async Task FailureBeforeRotationKeepsOriginalProfileAndRemovesDisposableCopy()
    {
        using var fixture = new Fixture();
        var service = new CodexAuthMaintenanceService(fixture.Paths, fixture.Auth,
            runRefreshServer: (_, _, _) => throw new IOException("server never refreshed"));
        await Assert.ThrowsAsync<IOException>(() => service.RefreshAsync(1));
        Assert.Equal("refresh-original", fixture.Auth.ReadCredentials(1).RefreshToken);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.LoginSessionsDirectory, "refresh-profile-*"));
    }

    [Fact]
    public async Task MismatchedAccountCannotReplaceCanonicalOrActiveCredentials()
    {
        using var fixture = new Fixture();
        var service = new CodexAuthMaintenanceService(fixture.Paths, fixture.Auth, runRefreshServer: (_, runtime, _) =>
        {
            WriteAuth(Path.Combine(runtime, "auth.json"), AuthFile("other", "other-account"));
            return Task.FromResult("validated=true");
        });
        await Assert.ThrowsAsync<CodexSyncBarException>(() => service.RefreshAsync(1));
        Assert.Equal("refresh-original", fixture.Auth.ReadCredentials(1).RefreshToken);
        Assert.Equal("account", fixture.Auth.ReadActiveAccountId());
        Assert.Single(service.RecoverPendingRefreshes());
    }

    [Fact]
    public async Task ConcurrentCanonicalRotationIsNeverOverwrittenByOlderTemporaryGeneration()
    {
        using var fixture = new Fixture();
        var service = new CodexAuthMaintenanceService(fixture.Paths, fixture.Auth, runRefreshServer: (_, runtime, _) =>
        {
            WriteAuth(Path.Combine(runtime, "auth.json"), AuthFile("candidate"));
            fixture.Import(AuthFile("concurrent"));
            return Task.FromResult("validated=true");
        });
        await Assert.ThrowsAsync<CodexSyncBarException>(() => service.RefreshAsync(1));
        Assert.Equal("refresh-concurrent", fixture.Auth.ReadCredentials(1).RefreshToken);
        Assert.Single(service.RecoverPendingRefreshes());
    }

    [Fact]
    public void StartupRecoversRotatedCredentialsLeftByHardProcessExit()
    {
        using var fixture = new Fixture();
        var runtime = Path.Combine(fixture.Paths.LoginSessionsDirectory, "refresh-profile-1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtime);
        var transactions = new AuthRefreshTransactionStore(fixture.Paths, fixture.Auth);
        transactions.Begin(runtime, 1, fixture.Auth.ReadAuthFile(fixture.Auth.ProfileAuthFile(1)));
        WriteAuth(Path.Combine(runtime, "auth.json"), AuthFile("recovered"));
        Assert.Empty(new AuthRefreshTransactionStore(fixture.Paths, fixture.Auth).Recover());
        Assert.Equal("refresh-recovered", fixture.Auth.ReadCredentials(1).RefreshToken);
        Assert.Equal("access-recovered", fixture.Auth.ReadActiveAuth()!.Tokens.AccessToken);
        Assert.False(Directory.Exists(runtime));
    }

    [Fact]
    public void StartupFinishesActiveAuthWriteAfterCanonicalRotationWasAlreadyCommitted()
    {
        using var fixture = new Fixture();
        var runtime = Path.Combine(fixture.Paths.LoginSessionsDirectory, "refresh-profile-1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtime);
        var transactions = new AuthRefreshTransactionStore(fixture.Paths, fixture.Auth);
        transactions.Begin(runtime, 1, fixture.Auth.ReadAuthFile(fixture.Auth.ProfileAuthFile(1)));
        WriteAuth(Path.Combine(runtime, "auth.json"), AuthFile("recovered"));
        fixture.Import(AuthFile("recovered"));
        Assert.Equal("access-original", fixture.Auth.ReadActiveAuth()!.Tokens.AccessToken);
        Assert.Empty(transactions.Recover());
        Assert.Equal("access-recovered", fixture.Auth.ReadActiveAuth()!.Tokens.AccessToken);
    }

    [Fact]
    public async Task SuccessfulValidatedRotationCommitsProfileAndActiveAuthTogether()
    {
        using var fixture = new Fixture();
        var service = new CodexAuthMaintenanceService(fixture.Paths, fixture.Auth, runRefreshServer: (_, runtime, _) =>
        {
            WriteAuth(Path.Combine(runtime, "auth.json"), AuthFile("new"));
            return Task.FromResult("validated=true");
        });
        var result = await service.RefreshAsync(1);
        Assert.True(result.DidRefresh);
        Assert.Equal("refresh-new", fixture.Auth.ReadCredentials(1).RefreshToken);
        Assert.Equal("access-new", fixture.Auth.ReadActiveAuth()!.Tokens.AccessToken);
    }

    private static CodexAuthFile AuthFile(string generation, string account = "account")
    {
        var claims = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"email\":\"test@example.com\"}"));
        return new CodexAuthFile
        {
            AuthMode = "chatgpt", Tokens = new()
            {
                AccountId = account, AccessToken = "access-" + generation, RefreshToken = "refresh-" + generation,
                IdToken = "id." + claims + ".signature",
            },
        };
    }

    private static void WriteAuth(string path, CodexAuthFile auth)
    {
        if (File.Exists(path)) File.Delete(path);
        WindowsPathSafety.WritePrivateBytes(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(auth)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-auth-refresh-" + Guid.NewGuid().ToString("N"));
        public WindowsPaths Paths { get; }
        public AuthStore Auth { get; }
        public Fixture()
        {
            Paths = new(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
            Paths.EnsureDirectories();
            Auth = new(Paths);
            Import(AuthFile("original"));
            Auth.SwitchActive(1);
        }
        public void Import(CodexAuthFile value)
        {
            var source = Path.Combine(_root, "incoming-" + Guid.NewGuid().ToString("N") + ".json");
            WriteAuth(source, value);
            Auth.ImportAuth(source, 1, replaceExisting: true);
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
}
