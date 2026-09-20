using System.Net;
using System.Text;
using System.Text.Json;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class ControllerUsageTests
{
    [Fact]
    public async Task HeadlessControllerSendsOnFreshWeeklyUsageAndNeverOnOfflineCache()
    {
        using var fixture = new Fixture();
        var fail = false;
        var calls = 0;
        var handler = new Handler(() => fail ? new(HttpStatusCode.ServiceUnavailable) : new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"plan_type":"plus","rate_limit":{"secondary_window":{"used_percent":0,"limit_window_seconds":604800}}}"""),
        });
        using var controller = new SyncBarController(fixture.Paths, new UsageService(new HttpClient(handler)), null, null,
            (_, _) => { calls++; return Task.FromResult("확인"); });
        await controller.WeeklyAnchor.SetEnabledAsync(1, true);
        await controller.RefreshUsageAsync();
        Assert.Equal(1, calls);
        Assert.NotNull(controller.WeeklyAnchor.Load().Records[1].LastSuccessAt);
        fail = true;
        await controller.RefreshUsageAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NetworkFailurePreservesLastUsageAndNeverCopiesResponseBodyToWidget()
    {
        using var fixture = new Fixture();
        var fail = false;
        var handler = new Handler(() => fail
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("sensitive-token-output") }
            : Success());
        using var controller = new SyncBarController(fixture.Paths, new UsageService(new HttpClient(handler)));
        var first = await controller.RefreshUsageAsync();
        fail = true;
        var last = await controller.RefreshUsageAsync();
        Assert.Equal(first.Accounts[0].Usage!.UpdatedAt, last.Accounts[0].Usage!.UpdatedAt);
        Assert.Equal(25, last.Accounts[0].Usage!.Session!.UsedPercent);
        Assert.NotNull(last.Accounts[0].Usage!.Error);
        Assert.DoesNotContain("sensitive-token-output", JsonSerializer.Serialize(last));
    }

    [Fact]
    public async Task ResponseFromOldCredentialGenerationCannotOverwriteLatestState()
    {
        using var fixture = new Fixture();
        var handler = new Handler(() =>
        {
            fixture.Import("rotated-access-token");
            return Success();
        });
        using var controller = new SyncBarController(fixture.Paths, new UsageService(new HttpClient(handler)));
        var result = await controller.RefreshUsageAsync();
        Assert.Null(result.Accounts[0].Usage);
        Assert.Null(controller.TryGetUsageSnapshot(1));
    }

    [Fact]
    public async Task FirstFailureReportsUnknownUsageAndOldWidgetConfigurationCannotSwitch()
    {
        using var fixture = new Fixture();
        using var controller = new SyncBarController(fixture.Paths,
            new UsageService(new HttpClient(new Handler(() => new(HttpStatusCode.ServiceUnavailable)))));
        var result = await controller.RefreshUsageAsync();
        Assert.Null(result.Accounts[0].Usage!.Session);
        Assert.NotNull(result.Accounts[0].Usage!.Error);
        await Assert.ThrowsAsync<CodexSyncBarException>(() => controller.SwitchAccountAsync(1, "stale-revision"));
        Assert.False((await controller.GetSnapshotAsync()).IsBusy);
    }

    [Fact]
    public async Task RestartedAppCanDisplayThePersistedLastUsageWhileOffline()
    {
        using var fixture = new Fixture();
        using (var first = new SyncBarController(fixture.Paths, new UsageService(new HttpClient(new Handler(Success)))))
            await first.RefreshUsageAsync();
        using var restarted = new SyncBarController(fixture.Paths,
            new UsageService(new HttpClient(new Handler(() => new(HttpStatusCode.ServiceUnavailable)))));
        await restarted.RefreshUsageAsync();
        Assert.Equal(25, restarted.TryGetUsageSnapshot(1)!.Session!.UsedPercent);
        Assert.NotNull(restarted.GetUsageError(1));
    }

    [Fact]
    public async Task CompletedLogoutConfigurationIsAcknowledgedBeforeLaterRelogin()
    {
        using var fixture = new Fixture();
        var auth = new AuthStore(fixture.Paths);
        var store = new ConfigurationStore(fixture.Paths);
        var configuration = store.LoadOrCreate();
        auth.DeleteProfile(1);
        var coordinator = new AccountSwitchCoordinator(fixture.Paths);
        await coordinator.SwitchAsync(2, [], removedProfileId: 1);

        coordinator.ReconcileCompletedLogout(store, configuration, auth);
        Assert.True(store.LoadOrCreate().Accounts.Single().NeedsLogin);
        Assert.True(JsonSerializer.Deserialize<SwitchJournal>(File.ReadAllText(coordinator.JournalPath))!.LogoutConfigurationCommitted);

        fixture.Import("new-login-access-token");
        store.UpdateAccountEmail(configuration, 1, "test@example.com");
        new AccountSwitchCoordinator(fixture.Paths).ReconcileCompletedLogout(store, store.LoadOrCreate(), auth);
        Assert.False(store.LoadOrCreate().Accounts.Single().NeedsLogin);
        Assert.Equal("new-login-access-token", auth.ReadCredentials(1).AccessToken);
    }

    [Fact]
    public async Task LegacyCompletedLogoutDoesNotReplayOverNewCredentials()
    {
        using var fixture = new Fixture();
        var coordinator = new AccountSwitchCoordinator(fixture.Paths);
        await coordinator.SwitchAsync(2, [], removedProfileId: 1);
        var store = new ConfigurationStore(fixture.Paths);
        fixture.Import("new-login-access-token");

        new AccountSwitchCoordinator(fixture.Paths).ReconcileCompletedLogout(store, store.LoadOrCreate(), new AuthStore(fixture.Paths));

        Assert.False(store.LoadOrCreate().Accounts.Single().NeedsLogin);
        Assert.True(JsonSerializer.Deserialize<SwitchJournal>(File.ReadAllText(coordinator.JournalPath))!.LogoutConfigurationCommitted);
    }

    [Fact]
    public async Task DeviceResponseStartedBeforeSwitchCannotBecomeFreshAfterSwitchCompletes()
    {
        using var fixture = new Fixture();
        fixture.AddDevice();
        var response = new TaskCompletionSource<IReadOnlyList<DashboardDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var controller = new SyncBarController(fixture.Paths, null, _ => [new SyntheticTarget()],
            (_, _) => ++calls == 1 ? response.Task : Task.FromResult<IReadOnlyList<DashboardDevice>>([Device(1)]));
        var staleRead = controller.RefreshDevicesAsync();
        var before = await controller.GetSnapshotAsync();
        var operation = await controller.SwitchAccountAsync(1, before.ConfigurationRevision);
        Assert.Equal("completed", (await controller.GetOperationStatusAsync(operation.Id))!.State);
        Assert.False((await controller.GetSnapshotAsync()).IsBusy);

        response.SetResult([Device(2)]);
        await staleRead;
        Assert.Null((await controller.GetSnapshotAsync()).Devices.Single(device => device.Id == "wsl:Ubuntu").ProfileId);

        await controller.RefreshDevicesAsync();
        Assert.Equal(1, (await controller.GetSnapshotAsync()).Devices.Single(device => device.Id == "wsl:Ubuntu").ProfileId);
    }

    [Fact]
    public async Task OlderDeviceRequestCannotOverwriteANewerCompletedRequest()
    {
        using var fixture = new Fixture();
        fixture.AddDevice();
        var first = new TaskCompletionSource<IReadOnlyList<DashboardDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var controller = new SyncBarController(fixture.Paths, null, null,
            (_, _) => ++calls == 1 ? first.Task : Task.FromResult<IReadOnlyList<DashboardDevice>>([Device(3)]));
        var older = controller.RefreshDevicesAsync();
        await controller.RefreshDevicesAsync();
        first.SetResult([Device(2)]);
        await older;
        Assert.Equal(3, (await controller.GetSnapshotAsync()).Devices.Single(device => device.Id == "wsl:Ubuntu").ProfileId);
    }

    private static DashboardDevice Device(int profileId) => new("wsl:Ubuntu", "Ubuntu", "wsl", profileId, true);

    private sealed class SyntheticTarget : IAccountTarget
    {
        public string Id => "wsl:Ubuntu";
        public string DisplayName => "Ubuntu";
        public string ConfigurationFingerprint => "synthetic-test-target";
        public bool IsLocal => false;
        public Task<string> PrepareAsync(int profileId, CancellationToken cancellationToken) => Task.FromResult("previous");
        public Task ApplyAsync(int profileId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task VerifyAsync(int profileId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RestoreAsync(string checkpoint, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReleaseAsync(string checkpoint, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":25,"limit_window_seconds":18000}},
             "rate_limit_reset_credits":{"available_count":0,"credits":[]}}
            """, Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-controller-tests-" + Guid.NewGuid().ToString("N"));
        public WindowsPaths Paths { get; }
        public Fixture()
        {
            Paths = new(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
            var store = new ConfigurationStore(Paths);
            var configuration = store.LoadOrCreate();
            store.UpdateAccountEmail(configuration, 1, "test@example.com");
            Import("initial-access-token");
        }

        public void Import(string accessToken)
        {
            Directory.CreateDirectory(_root);
            var source = Path.Combine(_root, "incoming-" + Guid.NewGuid().ToString("N") + ".auth.json");
            var claims = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"email\":\"test@example.com\"}"));
            WindowsPathSafety.WritePrivateBytes(source, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new CodexAuthFile
            {
                AuthMode = "chatgpt", Tokens = new() { AccessToken = accessToken, RefreshToken = "test-refresh", AccountId = "test-account", IdToken = "id." + claims + ".signature" },
            })));
            new AuthStore(Paths).ImportAuth(source, 1, replaceExisting: true);
        }
        public void AddDevice() => new WslConfigurationStore(Paths).Save(
            [new() { Distribution = "Ubuntu", Username = "synthetic", HomeDirectory = "/home/synthetic", IsInstalled = true, Enabled = true }]);
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
}
