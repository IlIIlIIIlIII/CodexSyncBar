using System.Net;
using System.Text;
using System.Text.Json;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Ubuntu.Tests;

public sealed class UbuntuUsageContractTests
{
    [Fact]
    public async Task FailedRefreshPreservesLastKnownUsageAcrossRestartWithoutLeakingResponse()
    {
        using var fixture = new Fixture();
        var fail = false;
        using (var controller = new SyncBarController(fixture.Paths,
            new UsageService(new HttpClient(new Handler(() => fail ? Failure() : Success())))))
        {
            var first = await controller.RefreshUsageAsync();
            fail = true;
            var last = await controller.RefreshUsageAsync();
            Assert.Equal(first.Accounts[0].Usage!.UpdatedAt, last.Accounts[0].Usage!.UpdatedAt);
            Assert.Equal(25, last.Accounts[0].Usage!.Weekly!.UsedPercent);
            Assert.NotNull(last.Accounts[0].Usage!.Error);
            Assert.DoesNotContain("synthetic-response-secret", JsonSerializer.Serialize(last));
        }
        using var restarted = new SyncBarController(fixture.Paths,
            new UsageService(new HttpClient(new Handler(Failure))));
        await restarted.RefreshUsageAsync();
        Assert.Equal(25, restarted.TryGetUsageSnapshot(1)!.Weekly!.UsedPercent);
        Assert.NotNull(restarted.GetUsageError(1));
    }

    [Fact]
    public async Task OldCredentialResponseCannotReplaceNewGeneration()
    {
        using var fixture = new Fixture();
        using var controller = new SyncBarController(fixture.Paths,
            new UsageService(new HttpClient(new Handler(() =>
            {
                fixture.Import("rotated-synthetic-access");
                return Success();
            }))));
        var result = await controller.RefreshUsageAsync();
        Assert.Null(result.Accounts[0].Usage);
        Assert.Null(controller.TryGetUsageSnapshot(1));
    }

    [Fact]
    public async Task OfflineFirstLoadIsUnknownAndStaleSwitchRevisionIsRejected()
    {
        using var fixture = new Fixture();
        using var controller = new SyncBarController(fixture.Paths,
            new UsageService(new HttpClient(new Handler(Failure))));
        var result = await controller.RefreshUsageAsync();
        Assert.Null(result.Accounts[0].Usage!.Weekly);
        Assert.NotNull(result.Accounts[0].Usage!.Error);
        await Assert.ThrowsAsync<CodexSyncBarException>(() => controller.SwitchAccountAsync(1, "stale-revision"));
        Assert.False((await controller.GetSnapshotAsync()).IsBusy);
    }

    [Fact]
    public void UbuntuPreferencesPreserveSupportedAndUnknownSettingsDuringMigration()
    {
        using var fixture = new Fixture();
        var store = new UsageDisplayPreferencesStore(fixture.Paths);
        Assert.Equal(new[] { UsageDisplayItem.CodexWeekly },
            store.LoadMenuPreferences().NormalizedItems());
        WindowsPathSafety.WritePrivateBytes(fixture.Paths.UsageDisplayPreferencesFile,
            """{"fiveHour":false,"codexWeekly":true,"sparkWeekly":false,"future":{"SparkWeekly":"preserve"}}"""u8.ToArray());
        var preferences = store.LoadUsagePreferences();
        Assert.True(preferences.CodexWeekly);
        store.SaveUsagePreferences(preferences);
        using var saved = JsonDocument.Parse(File.ReadAllText(fixture.Paths.UsageDisplayPreferencesFile));
        Assert.False(saved.RootElement.TryGetProperty("sparkWeekly", out _));
        Assert.False(saved.RootElement.TryGetProperty("fiveHour", out _));
        Assert.Equal("preserve", saved.RootElement.GetProperty("future").GetProperty("SparkWeekly").GetString());
    }

    [Fact]
    public void MenuTitleDistinguishesUnknownWeeklyQuotaFromEmptySelection()
    {
        var account = new AccountProfile { Id = 1, Email = "person@example.invalid", CustomAlias = "Work" };
        var snapshot = new UsageSnapshot(1, account.Email, "Pro",
            new UsageWindow(60, null, 604800), null, false, 0, [], DateTimeOffset.UtcNow);
        var items = new[] { UsageDisplayItem.CodexWeekly };
        Assert.Equal("Work 40%", MenuTitleFormatter.Title(account, snapshot, null, items, false, false));
        Assert.Equal("Work —", MenuTitleFormatter.Title(account, snapshot with { Weekly = null }, null, items, false, false));
        Assert.Equal("Work", MenuTitleFormatter.Title(account, snapshot, null, [], false, false));
    }

    [Theory]
    [InlineData("primary_window")]
    [InlineData("secondary_window")]
    public void WeeklyQuotaIsRecognizedInEitherApiSlot(string slot)
    {
        var credentials = new ProfileCredentials(1, "access", null, "refresh", "account", "test@example.invalid", null, "unused", false);
        var json = "{\"rate_limit\":{\"" + slot + "\":{\"used_percent\":25,\"limit_window_seconds\":604800}}}";
        var snapshot = UsageService.ParseUsagePayload(json, credentials);
        Assert.Equal(75, snapshot.Weekly!.RemainingPercent);
    }

    [Theory]
    [InlineData(18000)]
    [InlineData(86400)]
    [InlineData(0)]
    public void OtherDurationsNeverBecomeWeeklyQuota(int seconds)
    {
        var credentials = new ProfileCredentials(1, "access", null, "refresh", "account", "test@example.invalid", null, "unused", false);
        var json = "{\"rate_limit\":{\"primary_window\":{\"used_percent\":\"ignored\",\"limit_window_seconds\":" + seconds + "}}}";
        var snapshot = UsageService.ParseUsagePayload(json, credentials);
        Assert.Null(snapshot.Weekly);
        Assert.Null(snapshot.MenuRemainingPercent);
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":25,"limit_window_seconds":604800}}}"""),
    };
    private static HttpResponseMessage Failure() => new(HttpStatusCode.ServiceUnavailable)
    {
        Content = new StringContent("synthetic-response-secret"),
    };
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(response());
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-ubuntu-usage-" + Guid.NewGuid().ToString("N"));
        public WindowsPaths Paths { get; }
        public Fixture()
        {
            Paths = new(Path.Combine(_root, "home"), Path.Combine(_root, "data"));
            var store = new ConfigurationStore(Paths);
            var configuration = store.LoadOrCreate();
            store.UpdateAccountEmail(configuration, 1, "fixture@example.invalid");
            Import("synthetic-access");
        }
        public void Import(string access)
        {
            var source = Path.Combine(Paths.LoginSessionsDirectory, Guid.NewGuid().ToString("N") + ".json");
            var claims = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"email":"fixture@example.invalid"}"""));
            WindowsPathSafety.WritePrivateBytes(source, JsonSerializer.SerializeToUtf8Bytes(new CodexAuthFile
            {
                AuthMode = "chatgpt", Tokens = new() { AccessToken = access, RefreshToken = "synthetic-refresh",
                    AccountId = "fixture-account", IdToken = "id." + claims + ".signature" },
            }));
            new AuthStore(Paths).ImportAuth(source, 1, replaceExisting: true);
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
