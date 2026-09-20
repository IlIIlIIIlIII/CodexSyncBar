using System.Text.Json;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class UsagePreferencesMigrationTests
{
    [Fact]
    public void NewPreferencesDefaultToFiveHourAndWeeklyOnly()
    {
        using var fixture = new Fixture();
        var store = new UsageDisplayPreferencesStore(fixture.Paths);
        Assert.Equal(new[] { UsageDisplayItem.FiveHour, UsageDisplayItem.CodexWeekly }, Enum.GetValues<UsageDisplayItem>());
        Assert.Equal(Enum.GetValues<UsageDisplayItem>(), store.LoadMenuPreferences().NormalizedItems());
        Assert.All(Enum.GetValues<UsageDisplayItem>(), item => Assert.True(store.LoadUsagePreferences().IsVisible(item)));
    }

    [Fact]
    public void LegacyDisplayMigrationRemovesOnlyRetiredTopLevelKeys()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Paths.UsageDisplayPreferencesFile, """
            {"fiveHour":false,"codexWeekly":true,"sparkFiveHour":true,"SPARKWEEKLY":false,
             "futureOption":{"SparkWeekly":"unrelated nested data","enabled":true}}
            """);
        var store = new UsageDisplayPreferencesStore(fixture.Paths);
        var preferences = store.LoadUsagePreferences();
        Assert.False(preferences.FiveHour);
        Assert.True(preferences.CodexWeekly);
        using var migrated = JsonDocument.Parse(File.ReadAllText(fixture.Paths.UsageDisplayPreferencesFile));
        Assert.DoesNotContain(migrated.RootElement.EnumerateObject(), property => property.Name.StartsWith("spark", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("unrelated nested data", migrated.RootElement.GetProperty("futureOption").GetProperty("SparkWeekly").GetString());
        store.SaveUsagePreferences(preferences);
        Assert.Contains("futureOption", File.ReadAllText(fixture.Paths.UsageDisplayPreferencesFile));
    }

    [Fact]
    public void LegacyMenuMigrationPreservesSupportedSelectionAndUnrelatedSettings()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Paths.MenuBarUsagePreferencesFile, """
            {"items":["codexWeekly","sparkWeekly","futureSignal"],"futureOption":"keep"}
            """);
        var store = new UsageDisplayPreferencesStore(fixture.Paths);
        var preferences = store.LoadMenuPreferences();
        Assert.Equal(new[] { "codexWeekly", "futureSignal" }, preferences.Items);
        Assert.Equal(new[] { UsageDisplayItem.CodexWeekly }, preferences.NormalizedItems());
        store.SaveMenuPreferences(preferences);
        using var migrated = JsonDocument.Parse(File.ReadAllText(fixture.Paths.MenuBarUsagePreferencesFile));
        Assert.Equal("keep", migrated.RootElement.GetProperty("futureOption").GetString());
        Assert.DoesNotContain("spark", migrated.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("futureSignal", migrated.RootElement.GetRawText());
    }

    [Fact]
    public void RetiredOnlyMenuSelectionReceivesSupportedDefaults()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Paths.MenuBarUsagePreferencesFile, """{"items":["SparkFiveHour","sparkWeekly"]}""");
        var preferences = new UsageDisplayPreferencesStore(fixture.Paths).LoadMenuPreferences();
        Assert.Equal(new[] { UsageDisplayItem.FiveHour, UsageDisplayItem.CodexWeekly }, preferences.NormalizedItems());
        Assert.DoesNotContain("spark", File.ReadAllText(fixture.Paths.MenuBarUsagePreferencesFile), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("[\"codexWeekly\",\"fiveHour\"]", 2)]
    public void NonRetiredMenuSelectionsAreNotRewritten(string items, int count)
    {
        using var fixture = new Fixture();
        var original = "{\"items\":" + items + ",\"futureOption\":true}";
        File.WriteAllText(fixture.Paths.MenuBarUsagePreferencesFile, original);
        var preferences = new UsageDisplayPreferencesStore(fixture.Paths).LoadMenuPreferences();
        Assert.Equal(count, preferences.NormalizedItems().Count);
        Assert.Equal(original, File.ReadAllText(fixture.Paths.MenuBarUsagePreferencesFile));
    }

    [Fact]
    public void RetiredAdditionalQuotaPayloadCannotBreakSupportedUsage()
    {
        var credentials = new ProfileCredentials(1, "synthetic-access", null, "synthetic-refresh", "synthetic-account", "test@example.invalid", null, "unused", false);
        var usage = UsageService.ParseUsagePayload("""
            {"rate_limit":{"primary_window":{"used_percent":25,"limit_window_seconds":18000}},
             "additional_rate_limits":[{"limit_name":"Spark","rate_limit":{"primary_window":{"used_percent":"retired-invalid-value"}}}]}
            """, credentials);
        Assert.Equal(25, usage.Session!.UsedPercent);
        Assert.Null(usage.Weekly);
        Assert.DoesNotContain("spark", JsonSerializer.Serialize(usage), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyDashboardCacheRetainsSupportedWindowsWithoutRetiredFields()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Paths.StateRoot, "dashboard-usage.json"), """
            {"1":{"Session":{"UsedPercent":25,"ResetsAt":null,"DurationSeconds":18000},
                  "Weekly":{"UsedPercent":70,"ResetsAt":null,"DurationSeconds":604800},
                  "SparkSession":{"UsedPercent":10,"ResetsAt":null,"DurationSeconds":18000},
                  "SparkWeekly":{"UsedPercent":15,"ResetsAt":null,"DurationSeconds":604800},
                  "ResetCredits":2,"ResetCreditExpirations":[],"UpdatedAt":"2030-01-01T00:00:00Z"}}
            """);
        using var controller = new SyncBarController(fixture.Paths);
        var cached = controller.TryGetUsageSnapshot(1)!;
        Assert.Equal(25, cached.Session!.UsedPercent);
        Assert.Equal(70, cached.Weekly!.UsedPercent);
        Assert.Equal(2, cached.ResetCredits);
        Assert.DoesNotContain("spark", JsonSerializer.Serialize(cached), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HistoricalSparkTokenBucketsRemainAvailableWithUnknownPricing()
    {
        var summary = TokenUsageService.ParseSummary("""
            {"schemaVersion":6,"generatedAt":"2030-01-01T00:00:00Z","requests":1,"inputTokens":100,
             "outputTokens":20,"totalTokens":120,"buckets":[{"model":"gpt-5.3-codex-spark","serviceTier":"default",
             "inputTokens":100,"outputTokens":20,"totalTokens":120,"requests":1}],"errors":[]}
            """);
        Assert.Equal(120, summary.TotalTokens);
        Assert.Equal("gpt-5.3-codex-spark", summary.Buckets.Single().Model);
        Assert.False(TokenUsagePricing.EstimateUsd(summary.Buckets.Single()).IsPriced);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-quota-migration-" + Guid.NewGuid().ToString("N"));
        public WindowsPaths Paths { get; }
        public Fixture()
        {
            Paths = new WindowsPaths(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
            Assert.Equal(Path.Combine(_root, "home", ".codex", "auth.json"), Paths.ActiveAuthFile);
            Assert.Equal(Path.Combine(_root, "local", "CodexSyncBar"), Paths.StateRoot);
            Paths.EnsureDirectories();
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
}
