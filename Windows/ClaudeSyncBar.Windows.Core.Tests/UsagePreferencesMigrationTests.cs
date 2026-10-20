using System.Text.Json;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Windows.Core.Tests;

public sealed class UsagePreferencesMigrationTests
{
    [Fact]
    public void NewPreferencesShowBothClaudeQuotas()
    {
        using var fixture = new Fixture();
        var store = new UsageDisplayPreferencesStore(fixture.Paths);
        Assert.Equal(new[] { UsageDisplayItem.ClaudeWeekly, UsageDisplayItem.ClaudeFiveHour }, Enum.GetValues<UsageDisplayItem>());
        Assert.Equal(new[] { UsageDisplayItem.ClaudeWeekly }, store.LoadMenuPreferences().NormalizedItems());
        Assert.All(Enum.GetValues<UsageDisplayItem>(), item => Assert.True(store.LoadUsagePreferences().IsVisible(item)));
        Assert.True(store.LoadUsagePreferences().TaskbarUsage);
    }

    [Fact]
    public void TaskbarChoiceIsKeptAndAnOlderFileShowsTheTaskbar()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Paths.UsageDisplayPreferencesFile, """{"claudeWeekly":true,"claudeFiveHour":false}""");
        var store = new UsageDisplayPreferencesStore(fixture.Paths);
        var preferences = store.LoadUsagePreferences();
        Assert.True(preferences.TaskbarUsage);
        preferences.TaskbarUsage = false;
        store.SaveUsagePreferences(preferences);
        var saved = store.LoadUsagePreferences();
        Assert.False(saved.TaskbarUsage);
        Assert.False(saved.ClaudeFiveHour);
    }

    [Fact]
    public void LegacyDisplayMigrationRemovesOnlyRetiredTopLevelKeys()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Paths.UsageDisplayPreferencesFile, """
            {"fiveHour":false,"claudeWeekly":true,"sparkFiveHour":true,"SPARKWEEKLY":false,
             "futureOption":{"SparkWeekly":"unrelated nested data","enabled":true}}
            """);
        var store = new UsageDisplayPreferencesStore(fixture.Paths);
        var preferences = store.LoadUsagePreferences();
        Assert.True(preferences.ClaudeWeekly);
        // The Codex version's retired key is dropped; it never turns into the Claude five-hour setting.
        Assert.True(preferences.ClaudeFiveHour);
        using var migrated = JsonDocument.Parse(File.ReadAllText(fixture.Paths.UsageDisplayPreferencesFile));
        Assert.DoesNotContain(migrated.RootElement.EnumerateObject(), property => property.Name.Equals("fiveHour", StringComparison.OrdinalIgnoreCase));
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
            {"items":["claudeWeekly","sparkWeekly","futureSignal"],"futureOption":"keep"}
            """);
        var store = new UsageDisplayPreferencesStore(fixture.Paths);
        var preferences = store.LoadMenuPreferences();
        Assert.Equal(new[] { "claudeWeekly", "futureSignal" }, preferences.Items);
        Assert.Equal(new[] { UsageDisplayItem.ClaudeWeekly }, preferences.NormalizedItems());
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
        File.WriteAllText(fixture.Paths.MenuBarUsagePreferencesFile, """{"items":["FiveHour","SparkFiveHour","sparkWeekly"]}""");
        var preferences = new UsageDisplayPreferencesStore(fixture.Paths).LoadMenuPreferences();
        Assert.Equal(new[] { UsageDisplayItem.ClaudeWeekly }, preferences.NormalizedItems());
        Assert.DoesNotContain("spark", File.ReadAllText(fixture.Paths.MenuBarUsagePreferencesFile), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("[\"claudeWeekly\"]", 1)]
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
    public void OtherQuotaPeriodsAndAMalformedFiveHourEntryCannotBreakOrJoinWeeklyUsage()
    {
        var credentials = new ProfileCredentials(1, "synthetic-access", "synthetic-refresh", "synthetic-account", "test@example.invalid", null, "unused", false);
        var usage = UsageService.ParseUsagePayload("""
            {"five_hour":{"utilization":"not-a-number","resets_at":"2030-01-01T05:00:00+00:00"},
             "seven_day":{"utilization":25,"resets_at":"2030-01-07T00:00:00+00:00"},
             "seven_day_opus":{"utilization":80,"resets_at":null},
             "extra_usage":{"is_enabled":true,"used_credits":12}}
            """, credentials);
        Assert.Equal(25, usage.Weekly!.UsedPercent);
        Assert.Equal(604800, usage.Weekly.DurationSeconds);
        Assert.Null(usage.FiveHour);
        var serialized = JsonSerializer.Serialize(usage);
        Assert.DoesNotContain("18000", serialized);
        Assert.DoesNotContain(":80", serialized);
        Assert.DoesNotContain("credit", serialized, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal(70, cached.Weekly!.UsedPercent);
        Assert.DoesNotContain("spark", JsonSerializer.Serialize(cached), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Session", JsonSerializer.Serialize(cached), StringComparison.OrdinalIgnoreCase);
        var saved = File.ReadAllText(Path.Combine(fixture.Paths.StateRoot, "dashboard-usage.json"));
        Assert.DoesNotContain("Session", saved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Spark", saved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ResetCredit", saved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TokenBucketsOfUnknownModelsRemainAvailableWithUnknownPricing()
    {
        var summary = TokenUsageService.ParseSummary("""
            {"schemaVersion":6,"generatedAt":"2030-01-01T00:00:00Z","requests":1,"inputTokens":100,
             "outputTokens":20,"totalTokens":120,"buckets":[{"model":"claude-future-model","serviceTier":"default",
             "inputTokens":100,"outputTokens":20,"totalTokens":120,"requests":1}],"errors":[]}
            """);
        Assert.Equal(120, summary.TotalTokens);
        Assert.Equal("claude-future-model", summary.Buckets.Single().Model);
        Assert.False(TokenUsagePricing.EstimateUsd(summary.Buckets.Single()).IsPriced);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-quota-migration-" + Guid.NewGuid().ToString("N"));
        public WindowsPaths Paths { get; }
        public Fixture()
        {
            Paths = new WindowsPaths(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
            Assert.Equal(Path.Combine(_root, "home", ".claude", ".credentials.json"), Paths.ActiveAuthFile);
            Assert.Equal(Path.Combine(_root, "local"), Path.GetDirectoryName(Paths.StateRoot));
            Paths.EnsureDirectories();
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
}
