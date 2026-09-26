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
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var controller = new SyncBarController(fixture.Paths, null, _ => [new SyntheticTarget()],
            (_, _) => { started.TrySetResult(); return ++calls == 1 ? response.Task : Task.FromResult<IReadOnlyList<DashboardDevice>>([Device(1)]); });
        var staleRead = controller.RefreshDevicesAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var before = await controller.GetSnapshotAsync();
        var operation = await controller.SwitchAccountAsync(1, before.ConfigurationRevision);
        Assert.Equal("completed", (await WaitForOperationAsync(controller, operation.Id)).State);
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
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var controller = new SyncBarController(fixture.Paths, null, null,
            (_, _) => { started.TrySetResult(); return ++calls == 1 ? first.Task : Task.FromResult<IReadOnlyList<DashboardDevice>>([Device(3)]); });
        var older = controller.RefreshDevicesAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await controller.RefreshDevicesAsync();
        first.SetResult([Device(2)]);
        await older;
        Assert.Equal(3, (await controller.GetSnapshotAsync()).Devices.Single(device => device.Id == "wsl:Ubuntu").ProfileId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleSshSwitchOnlyTouchesSelectedDeviceIncludingRollback(bool failVerification)
    {
        using var fixture = new Fixture();
        var store = new ConfigurationStore(fixture.Paths);
        var configuration = store.LoadOrCreate();
        configuration.Devices.Add(new() { Id = "chosen", DisplayName = "Chosen SSH", Host = "chosen.test", Username = "test", Enabled = true });
        configuration.Devices.Add(new() { Id = "other", DisplayName = "Other SSH", Host = "other.test", Username = "test", Enabled = true });
        store.Save(configuration);
        var chosen = new SyntheticTarget { Id = "ssh:chosen", FailVerification = failVerification };
        var others = new[] { new SyntheticTarget { Id = "ssh:other" }, new SyntheticTarget(), new SyntheticTarget { Id = "windows", IsLocal = true } };
        using var controller = new SyncBarController(fixture.Paths, null, _ => [chosen, .. others], null);
        var snapshot = await controller.GetSnapshotAsync();
        var operation = await controller.SwitchSshDeviceAccountAsync(1, "chosen", snapshot.ConfigurationRevision);
        var result = await WaitForOperationAsync(controller, operation.Id);

        Assert.Equal(failVerification ? "failed" : "completed", result.State);
        Assert.Equal("ssh:chosen", Assert.Single(result.Targets).Id);
        Assert.Contains("apply", chosen.Calls);
        Assert.Contains("verify", chosen.Calls);
        Assert.Equal(failVerification, chosen.Calls.Contains("restore"));
        Assert.All(others, target => Assert.Empty(target.Calls));
        Assert.Null(new AuthStore(fixture.Paths).ReadActiveAccountId());
    }

    [Theory]
    [InlineData("missing", true, false)]
    [InlineData("chosen", false, false)]
    [InlineData("chosen", true, true)]
    [InlineData("windows", true, false)]
    public async Task SingleSshSwitchRejectsInvalidDeviceOrStaleConfiguration(string deviceId, bool enabled, bool stale)
    {
        using var fixture = new Fixture();
        var store = new ConfigurationStore(fixture.Paths);
        var configuration = store.LoadOrCreate();
        configuration.Devices.Add(new() { Id = "chosen", DisplayName = "Chosen SSH", Host = "chosen.test", Username = "test", Enabled = enabled });
        store.Save(configuration);
        var target = new SyntheticTarget { Id = "ssh:chosen" };
        using var controller = new SyncBarController(fixture.Paths, null, _ => [target], null);
        var snapshot = await controller.GetSnapshotAsync();
        await Assert.ThrowsAsync<CodexSyncBarException>(() => controller.SwitchSshDeviceAccountAsync(1, deviceId,
            stale ? "stale-revision" : snapshot.ConfigurationRevision));
        Assert.Empty(target.Calls);
        Assert.False((await controller.GetSnapshotAsync()).IsBusy);
    }

    private static DashboardDevice Device(int profileId) => new("wsl:Ubuntu", "Ubuntu", "wsl", profileId, true);

    private static async Task<SwitchOperation> WaitForOperationAsync(SyncBarController controller, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var operation = (await controller.GetOperationStatusAsync(id))!;
            if (operation.IsComplete && !(await controller.GetSnapshotAsync()).IsBusy) return operation;
            await Task.Delay(10, deadline.Token);
        }
    }

    [Fact]
    public async Task SlowSwitchPreparationDoesNotBlockQueueOrDashboardReads()
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var controller = new SyncBarController(fixture.Paths, null, _ =>
        {
            Assert.Null(SynchronizationContext.Current);
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return [new SyntheticTarget()];
        }, null);
        var snapshot = await controller.GetSnapshotAsync();
        try
        {
            var queued = await controller.SwitchAccountAsync(1, snapshot.ConfigurationRevision).WaitAsync(TimeSpan.FromSeconds(5));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True((await controller.GetSnapshotAsync().WaitAsync(TimeSpan.FromSeconds(5))).IsBusy);
            release.Set();
            Assert.Equal("completed", (await WaitForOperationAsync(controller, queued.Id)).State);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task DeviceAndUsageQueriesDoNotRunOnCallerSynchronizationContext()
    {
        using var fixture = new Fixture();
        var contexts = new List<SynchronizationContext?>();
        using var controller = new SyncBarController(fixture.Paths,
            new UsageService(new HttpClient(new Handler(() => { contexts.Add(SynchronizationContext.Current); return Success(); }))),
            null, (_, _) => { contexts.Add(SynchronizationContext.Current); return Task.FromResult<IReadOnlyList<DashboardDevice>>([]); });
        await controller.RefreshDevicesAsync();
        await controller.RefreshUsageAsync();
        Assert.Equal(2, contexts.Count);
        Assert.All(contexts, context => Assert.Null(context));
    }

    private sealed class SyntheticTarget : IAccountTarget
    {
        public string Id { get; init; } = "wsl:Ubuntu";
        public string DisplayName => "Ubuntu";
        public string ConfigurationFingerprint => "synthetic-test-target";
        public bool IsLocal { get; init; }
        public bool FailVerification { get; init; }
        public List<string> Calls { get; } = [];
        public Task<string> PrepareAsync(int profileId, CancellationToken cancellationToken) { Calls.Add("prepare"); return Task.FromResult("previous"); }
        public Task ApplyAsync(int profileId, CancellationToken cancellationToken) { Calls.Add("apply"); return Task.CompletedTask; }
        public Task VerifyAsync(int profileId, CancellationToken cancellationToken)
        {
            Calls.Add("verify");
            if (FailVerification) throw new CodexSyncBarException("Synthetic verification failure");
            return Task.CompletedTask;
        }
        public Task RestoreAsync(string checkpoint, CancellationToken cancellationToken) { Calls.Add("restore"); return Task.CompletedTask; }
        public Task ReleaseAsync(string checkpoint, CancellationToken cancellationToken) { Calls.Add("release"); return Task.CompletedTask; }
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
