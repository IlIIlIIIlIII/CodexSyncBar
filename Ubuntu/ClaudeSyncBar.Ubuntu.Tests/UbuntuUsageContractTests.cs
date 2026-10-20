using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeSyncBar.Windows.Core;
using Contracts = ClaudeSyncBar.Windows.Core.Tests.TestAuth;

namespace ClaudeSyncBar.Ubuntu.Tests;

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
            Assert.Equal(3, last.Accounts[0].Usage!.FiveHour!.UsedPercent);
            Assert.NotNull(last.Accounts[0].Usage!.Error);
            Assert.DoesNotContain("synthetic-response-secret", JsonSerializer.Serialize(last));
        }
        using var restarted = new SyncBarController(fixture.Paths,
            new UsageService(new HttpClient(new Handler(Failure))));
        await restarted.RefreshUsageAsync();
        Assert.Equal(25, restarted.TryGetUsageSnapshot(1)!.Weekly!.UsedPercent);
        // The cache written before a restart keeps the five-hour quota as well.
        Assert.Equal(3, restarted.TryGetUsageSnapshot(1)!.FiveHour!.UsedPercent);
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
        await Assert.ThrowsAsync<ClaudeSyncBarException>(() => controller.SwitchAccountAsync(1, "stale-revision"));
        Assert.False((await controller.GetSnapshotAsync()).IsBusy);
    }

    [Fact]
    public void UbuntuPreferencesPreserveSupportedAndUnknownSettingsDuringMigration()
    {
        using var fixture = new Fixture();
        var store = new UsageDisplayPreferencesStore(fixture.Paths);
        Assert.Equal(new[] { UsageDisplayItem.ClaudeWeekly },
            store.LoadMenuPreferences().NormalizedItems());
        WindowsPathSafety.WritePrivateBytes(fixture.Paths.UsageDisplayPreferencesFile,
            """{"fiveHour":false,"claudeWeekly":true,"sparkWeekly":false,"future":{"SparkWeekly":"preserve"}}"""u8.ToArray());
        var preferences = store.LoadUsagePreferences();
        Assert.True(preferences.ClaudeWeekly);
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
            new UsageWindow(60, null, 604800), DateTimeOffset.UtcNow);
        var items = new[] { UsageDisplayItem.ClaudeWeekly };
        Assert.Equal("Work 40%", MenuTitleFormatter.Title(account, snapshot, null, items, false, false));
        Assert.Equal("Work —", MenuTitleFormatter.Title(account, snapshot with { Weekly = null }, null, items, false, false));
        Assert.Equal("Work", MenuTitleFormatter.Title(account, snapshot, null, [], false, false));
    }

    [Theory]
    [InlineData("25", 75)]
    [InlineData("25.4", 74.6)]
    [InlineData("\"25\"", 75)]
    [InlineData("0", 100)]
    [InlineData("100", 0)]
    public void WeeklyQuotaComesFromTheSevenDayEntry(string utilization, double remaining)
    {
        var credentials = new ProfileCredentials(1, "access", "refresh", "account", "test@example.invalid", null, "unused", false, "Max 5x");
        var json = "{\"five_hour\":{\"utilization\":90,\"resets_at\":null},\"seven_day\":{\"utilization\":" + utilization
            + ",\"resets_at\":\"2030-01-07T03:59:59.943679+00:00\"}}";
        var snapshot = UsageService.ParseUsagePayload(json, credentials);
        Assert.Equal(remaining, snapshot.Weekly!.RemainingPercent, 3);
        Assert.Equal(604800, snapshot.Weekly.DurationSeconds);
        Assert.Equal(new DateTimeOffset(2030, 1, 7, 3, 59, 59, TimeSpan.Zero).AddTicks(9436790), snapshot.Weekly.ResetsAt);
        Assert.Equal("Max 5x", snapshot.Plan);
        // The five-hour entry is its own quota, never mixed into the weekly one.
        Assert.Equal(10, snapshot.FiveHour!.RemainingPercent, 3);
        Assert.Equal(18000, snapshot.FiveHour.DurationSeconds);
        Assert.Null(snapshot.FiveHour.ResetsAt);
    }

    [Fact]
    public void FiveHourQuotaComesFromTheFiveHourEntry()
    {
        var credentials = new ProfileCredentials(1, "access", "refresh", "account", "test@example.invalid", null, "unused", false);
        var snapshot = UsageService.ParseUsagePayload(
            "{\"five_hour\":{\"utilization\":37.5,\"resets_at\":\"2030-01-07T05:00:00+00:00\"},\"seven_day\":null}", credentials);
        Assert.Equal(62.5, snapshot.FiveHour!.RemainingPercent, 3);
        Assert.Equal(new DateTimeOffset(2030, 1, 7, 5, 0, 0, TimeSpan.Zero), snapshot.FiveHour.ResetsAt);
        Assert.Null(snapshot.Weekly);
    }

    [Theory]
    [InlineData("{\"five_hour\":{\"utilization\":40,\"resets_at\":null}}")]
    [InlineData("{\"seven_day\":null,\"seven_day_opus\":{\"utilization\":40,\"resets_at\":null}}")]
    [InlineData("{\"seven_day\":{\"utilization\":null,\"resets_at\":null}}")]
    [InlineData("{}")]
    public void OtherPeriodsNeverBecomeWeeklyQuota(string json)
    {
        var credentials = new ProfileCredentials(1, "access", "refresh", "account", "test@example.invalid", null, "unused", false);
        var snapshot = UsageService.ParseUsagePayload(json, credentials);
        Assert.Null(snapshot.Weekly);
        Assert.Null(snapshot.MenuRemainingPercent);
        Assert.Equal("Claude", snapshot.Plan);
        // Model-specific weekly entries are not a five-hour quota either.
        Assert.Equal(json.Contains("five_hour"), snapshot.FiveHour is not null);
    }

    [Fact]
    public async Task UsageRequestCarriesTheOAuthTokenAndBetaHeader()
    {
        HttpRequestMessage? sent = null;
        var service = new UsageService(new HttpClient(new Handler(() => Success(), request => sent = request)));
        var credentials = new ProfileCredentials(1, "synthetic-access", "refresh", "account", "test@example.invalid", null, "unused", false);
        var snapshot = await service.FetchAsync(credentials);
        Assert.Equal(75, snapshot.Weekly!.RemainingPercent);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", sent!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", sent.Headers.Authorization!.Scheme);
        Assert.Equal("synthetic-access", sent.Headers.Authorization.Parameter);
        Assert.Equal("oauth-2025-04-20", Assert.Single(sent.Headers.GetValues("anthropic-beta")));

        var expired = new UsageService(new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => expired.FetchAsync(credentials));
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"five_hour":{"utilization":3,"resets_at":null},"seven_day":{"utilization":25,"resets_at":"2030-01-07T00:00:00+00:00"}}"""),
    };
    private static HttpResponseMessage Failure() => new(HttpStatusCode.ServiceUnavailable)
    {
        Content = new StringContent("synthetic-response-secret"),
    };
    private sealed class Handler(Func<HttpResponseMessage> response, Action<HttpRequestMessage>? observe = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            observe?.Invoke(request);
            return Task.FromResult(response());
        }
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
            WindowsPathSafety.WritePrivateBytes(source, Contracts.Bytes(
                Contracts.Create("fixture-account", access, "synthetic-refresh", email: "fixture@example.invalid")));
            new AuthStore(Paths).ImportAuth(source, 1, replaceExisting: true);
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
