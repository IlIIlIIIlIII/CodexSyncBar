using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Windows.Core.Tests;

/// <summary>Throughput over a period and quota use per million tokens, from minutes and quota readings.</summary>
public sealed class TokenRateTests : IDisposable
{
    private static readonly DateTimeOffset End = new(2026, 10, 24, 14, 28, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-rates-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static long MinuteOf(DateTimeOffset time) => (long)Math.Floor(time.ToUnixTimeMilliseconds() / 60_000d);

    // A minute of opus tokens: cache reads, cache writes and output; uncached is everything but the reads.
    private static long[] Row(DateTimeOffset at, long cacheRead, long cacheWrite, long output, int model = 0) =>
        [MinuteOf(at), model, 0, 0, cacheRead + cacheWrite, cacheRead, cacheWrite, 0, output, output / 2, 1];

    private static DeviceTokenUsageSummary Summary(IEnumerable<long[]> rows)
    {
        var minutes = rows.OrderBy(row => row[0]).ToList();
        return new DeviceTokenUsageSummary
        {
            SchemaVersion = 7, Models = ["claude-opus-5-5", "claude-sonnet-5-5"], Tiers = ["default"], Minutes = minutes,
            TotalTokens = minutes.Sum(row => row[4] + row[8]),
        };
    }

    private static TokenDeviceInput Local(IEnumerable<long[]> rows) => new("local", "이 PC", "local", true, null, Summary(rows));

    // One reading every five minutes from 'from' to the end, the five-hour use given by the reading's index.
    private static List<QuotaHistoryEntry> Readings(DateTimeOffset from, Func<int, double?> fiveHour, Func<int, DateTimeOffset?>? reset = null,
        Func<int, int?>? holder = null, int profileId = 1, Func<int, double?>? weekly = null, Func<int, string?>? plan = null)
    {
        var entries = new List<QuotaHistoryEntry>();
        var index = 0;
        for (var at = from; at <= End; at += TimeSpan.FromMinutes(5), index++)
        {
            var i = index;
            entries.Add(new QuotaHistoryEntry
            {
                Kind = QuotaHistoryEntry.Poll, At = at,
                Samples = [new QuotaSample(profileId, at, plan?.Invoke(i) ?? "Max 20x", fiveHour(i), reset?.Invoke(i) ?? End.AddHours(3), weekly?.Invoke(i) ?? 10, End.AddDays(3))],
                Devices = [new DeviceAccountObservation("local", holder is null ? profileId : holder(i), at)],
            });
        }

        return entries;
    }

    // A million uncached tokens in the middle of every five-minute pair.
    private static IEnumerable<long[]> Steady(DateTimeOffset from, long uncached = 1_000_000, long cacheRead = 20_000_000)
    {
        for (var at = from + TimeSpan.FromMinutes(2); at < End; at += TimeSpan.FromMinutes(5))
            yield return Row(at, cacheRead, uncached - 100_000, 100_000);
    }

    private static TokenPeriodView Calculate(IReadOnlyList<TokenDeviceInput> devices, IReadOnlyList<QuotaHistoryEntry> history,
        int hours = 2, string basis = TokenBurnBasis.Uncached, bool fiveHour = true, bool weekly = true) =>
        TokenRateCalculator.Calculate(new TokenRateRequest(End, hours, basis, devices, history, history.FirstOrDefault()?.At,
            [new TokenAccountInput(1, "업무 계정", "Max 20x"), new TokenAccountInput(2, "개인 계정", "Max 5x")], 1, fiveHour, weekly));

    [Fact]
    public void ThroughputDividesByTheHoursSinceTheFirstRecordWhenThePeriodIsLonger()
    {
        // Three hours of records, 6M tokens an hour, looked at over a day.
        var rows = Enumerable.Range(0, 180).Select(i => Row(End.AddMinutes(-180 + i), 90_000, 5_000, 5_000)).ToList();
        var view = Calculate([Local(rows)], [], hours: 24);

        Assert.True(view.Clipped);
        Assert.Equal(3, view.ElapsedHours, 3);
        Assert.Equal(6_000_000, view.Throughput.Total, 0);
        Assert.Equal(300_000, view.Throughput.Output, 0);
        Assert.Equal(600_000, view.Throughput.Uncached, 0);
        Assert.Equal(5_400_000, view.Throughput.CacheRead, 0);
        Assert.Equal(3, view.ActiveHours);
        Assert.Equal(18_000_000, view.Totals.TotalTokens);
    }

    [Fact]
    public void APeriodOfOneHourCountsOnlyItsOwnMinutes()
    {
        var rows = new[] { Row(End.AddMinutes(-90), 1_000, 0, 0), Row(End.AddMinutes(-30), 2_000, 0, 0), Row(End.AddMinutes(-1), 3_000, 0, 0) };
        var view = Calculate([Local(rows)], [], hours: 1);

        Assert.False(view.Clipped);
        Assert.Equal(1, view.ElapsedHours);
        Assert.Equal(5_000, view.Totals.TotalTokens);
        Assert.Equal(End.AddHours(-1), view.StartedAt);
        Assert.Equal(1, view.ActiveHours);
    }

    [Fact]
    public void TheRateIsTheSummedRiseOverTheSummedTokens()
    {
        var from = End.AddHours(-2);
        var view = Calculate([Local(Steady(from))], Readings(from, i => 10 + i * 2.0));

        var account = Assert.Single(view.Burn.Accounts);
        Assert.True(account.IsCurrent);
        Assert.Equal("ok", account.FiveHour.State);
        Assert.Equal(2, account.FiveHour.RatePerMillion!.Value, 6);
        Assert.Equal(1, account.FiveHour.Runs);
        Assert.Equal(1, account.FiveHour.Windows);
        Assert.Equal(1, view.Burn.AttributedTokens / view.Burn.BasisTokens, 6);
        Assert.Equal(["개인 계정"], view.Burn.IdleAccounts);
    }

    [Fact]
    public void AResetGapOrLowerReadingBreaksTheComparison()
    {
        var from = End.AddHours(-2);
        // Window resets after reading 6; reading 12 is missing (a gap of ten minutes is fine, of twenty is not); reading 18 is lower.
        var readings = Readings(from,
            i => i == 18 ? 1 : i < 7 ? 50 + i * 2.0 : i * 2.0,
            reset: i => i < 7 ? from.AddMinutes(33) : End.AddHours(4))
            .Where((_, i) => i is not (12 or 13 or 14)).ToList();
        var view = Calculate([Local(Steady(from))], readings);

        var five = Assert.Single(view.Burn.Accounts).FiveHour;
        // Of the 21 pairs left, the reset, the gap and both sides of the drop leave 17.
        Assert.Equal(2, five.RatePerMillion!.Value, 6);
        Assert.Equal(2, five.Windows);
        Assert.True(five.Runs >= 3);
        Assert.Equal(2 * 17, five.DeltaPercent, 6);
    }

    [Fact]
    public void TokensOfAnUnknownAccountKeepThatStretchOut()
    {
        var from = End.AddHours(-2);
        // For half an hour the app could not tell which account this device used.
        var view = Calculate([Local(Steady(from))], Readings(from, i => 10 + i * 2.0, holder: i => i is >= 6 and < 12 ? null : 1));

        var five = Assert.Single(view.Burn.Accounts).FiveHour;
        Assert.Equal(2, five.RatePerMillion!.Value, 6);
        Assert.Equal(2, five.Runs);
        Assert.InRange(view.Burn.AttributedTokens / view.Burn.BasisTokens, 0.7, 0.8);
    }

    [Fact]
    public void UseWithNoTokensNearItIsCountedApart()
    {
        var from = End.AddHours(-2);
        // Tokens stop after the first hour; the use rises by 5 later with nothing in the logs.
        var rows = Steady(from).Where(row => row[0] < MinuteOf(from.AddHours(1)));
        var view = Calculate([Local(rows)], Readings(from, i => i <= 12 ? 10 + i * 2.0 : i == 20 ? 39 : i > 20 ? 39 : 34));

        var five = Assert.Single(view.Burn.Accounts).FiveHour;
        Assert.Equal(5, five.UntrackedPercent, 6);
        Assert.Equal(2, five.RatePerMillion!.Value, 6);
    }

    [Fact]
    public void ARiseJustAfterTheTokensStaysInTheRate()
    {
        var from = End.AddHours(-2);
        // The service shows use a little late: the last tokens land in one pair, the rise in the next.
        var rows = Steady(from).Where(row => row[0] < MinuteOf(from.AddMinutes(60)));
        var view = Calculate([Local(rows)], Readings(from, i => i <= 12 ? 10 + i * 2.0 : i == 13 ? 36 : 36));

        var five = Assert.Single(view.Burn.Accounts).FiveHour;
        Assert.Equal(0, five.UntrackedPercent);
        Assert.Equal(26, five.DeltaPercent, 6);
    }

    [Fact]
    public void AWeeklyMessageTheAppSentLeavesItsPairOut()
    {
        var from = End.AddHours(-2);
        var history = Readings(from, i => 10 + i * 2.0);
        history.Add(new QuotaHistoryEntry { Kind = QuotaHistoryEntry.Anchor, At = from.AddMinutes(32), ProfileId = 1 });
        var view = Calculate([Local(Steady(from))], history.OrderBy(entry => entry.At).ToList());

        var five = Assert.Single(view.Burn.Accounts).FiveHour;
        Assert.Equal(2, five.Runs);
        Assert.Equal(2, five.RatePerMillion!.Value, 6);
    }

    [Fact]
    public void ARateHoldsForThePlanTheAccountHasNow()
    {
        var from = End.AddHours(-2);
        var view = Calculate([Local(Steady(from))], Readings(from, i => i < 12 ? 10 + i * 8.0 : i * 2.0, plan: i => i < 12 ? "Max 5x" : "Max 20x"));

        var five = Assert.Single(view.Burn.Accounts).FiveHour;
        Assert.True(five.PlanChanged);
        Assert.Equal(2, five.RatePerMillion!.Value, 6);
    }

    [Fact]
    public void SmallOrCoarseChangesAreNotShownAsARate()
    {
        var from = End.AddHours(-1);
        var view = Calculate([Local(Steady(from))], Readings(from, i => i < 6 ? 10 : 12, weekly: i => i < 3 ? 10 : i < 9 ? 11 : 15), hours: 1);

        var account = Assert.Single(view.Burn.Accounts);
        Assert.Equal("collecting", account.FiveHour.State);
        Assert.Null(account.FiveHour.RatePerMillion);
        Assert.Equal(2, account.FiveHour.DeltaPercent);
        // Five points in one run of whole-percent readings: one point of rounding in five.
        Assert.Equal("ok", account.Weekly.State);
        var rough = Calculate([Local(Steady(from))], Readings(from, i => 10, weekly: i => i < 3 ? 10 : i == 3 ? 12 : i < 6 ? 13 : i == 6 ? 0 : 1), hours: 1)
            .Burn.Accounts.Single().Weekly;
        Assert.Equal("low", rough.State);
    }

    [Fact]
    public void AHiddenQuotaIsNotWorkedOut()
    {
        var from = End.AddHours(-2);
        var account = Calculate([Local(Steady(from))], Readings(from, i => 10 + i * 2.0), fiveHour: false).Burn.Accounts.Single();
        Assert.Equal("hidden", account.FiveHour.State);
        Assert.Null(account.FiveHour.RatePerMillion);
    }

    [Fact]
    public void ASwitchMovesTheDeviceAndAnUnexplainedChangeIsUnknown()
    {
        var from = End.AddHours(-2);
        var history = Readings(from, i => 10 + i * 2.0, holder: i => i < 12 ? 1 : 2);
        // Without a switch the change happened between two looks: that stretch belongs to nobody.
        var unexplained = Calculate([Local(Steady(from))], history).Burn;
        Assert.InRange(unexplained.AttributedTokens / unexplained.BasisTokens, 0.95, 0.97);

        history.Add(new QuotaHistoryEntry { Kind = QuotaHistoryEntry.Switch, At = from.AddMinutes(58), ProfileId = 2, DeviceIds = ["local"] });
        var switched = Calculate([Local(Steady(from))], history.OrderBy(entry => entry.At).ToList()).Burn;
        Assert.Equal(1, switched.AttributedTokens / switched.BasisTokens, 6);
        Assert.Equal(2, switched.Accounts.Count);
        Assert.Equal(["이 PC"], switched.Accounts.Single(account => account.ProfileId == 2).Devices);
    }

    [Fact]
    public void TheBasisChangesWhatAMillionTokensIs()
    {
        var from = End.AddHours(-2);
        var history = Readings(from, i => 10 + i * 2.0);
        var uncached = Calculate([Local(Steady(from))], history).Burn.Accounts.Single().FiveHour.RatePerMillion!.Value;
        var total = Calculate([Local(Steady(from))], history, basis: TokenBurnBasis.Total).Burn.Accounts.Single().FiveHour.RatePerMillion!.Value;
        var weighted = Calculate([Local(Steady(from))], history, basis: TokenBurnBasis.Weighted).Burn.Accounts.Single().FiveHour.RatePerMillion!.Value;

        Assert.Equal(2, uncached, 6);
        Assert.Equal(2 / 21d, total, 6);
        // Opus 5.5: reads at 0.05, five-minute writes at 1.25 and output at 5 input tokens each.
        Assert.Equal(2 / ((20_000_000 * 0.05 + 900_000 * 1.25 + 100_000 * 5) / 1_000_000d), weighted, 6);
        Assert.Equal(TokenBurnBasis.Uncached, TokenBurnBasis.Normalize("anything"));
    }

    [Fact]
    public void ADeviceWhoseLogsWereNotReadKeepsItsAccountsStretchesOut()
    {
        var from = End.AddHours(-2);
        var history = Readings(from, i => 10 + i * 2.0);
        history.Add(new QuotaHistoryEntry { Kind = QuotaHistoryEntry.Poll, At = from.AddMinutes(-1), Devices = [new("ssh:dev", 1, from.AddMinutes(-1))] });
        var view = Calculate([Local(Steady(from)), new TokenDeviceInput("ssh:dev", "개발 서버", "ssh", false, "사용량 수집을 완료하지 못했습니다.", null)],
            history.OrderBy(entry => entry.At).ToList());

        Assert.Equal("collecting", view.Burn.Accounts.Single().FiveHour.State);
        var device = view.Devices.Single(device => device.Id == "ssh:dev");
        Assert.False(device.HasMinutes);
        Assert.Equal("사용량 수집을 완료하지 못했습니다.", device.Error);
    }

    [Fact]
    public void ADeviceOnAnAccountNotRegisteredHereBlocksNoRate()
    {
        var from = End.AddHours(-2);
        var server = new TokenDeviceInput("ssh:build", "빌드 서버", "ssh", true, null, Summary(Steady(from)));
        var history = Readings(from, i => 10 + i * 2.0);
        history.Add(new QuotaHistoryEntry { Kind = QuotaHistoryEntry.Poll, At = from.AddMinutes(-1), Devices = [new("ssh:build", null, from.AddMinutes(-1), Unregistered: true)] });
        var view = Calculate([Local(Steady(from)), server], history.OrderBy(entry => entry.At).ToList());

        var five = view.Burn.Accounts.Single().FiveHour;
        Assert.Equal("ok", five.State);
        Assert.Equal(2, five.RatePerMillion!.Value, 6);
        Assert.Equal(view.Burn.BasisTokens / 2, view.Burn.UnregisteredTokens, 6);

        // Not known at all, the same device keeps every stretch out.
        history[^1] = history[^1] with { Devices = [new("ssh:build", null, from.AddMinutes(-1))] };
        Assert.Equal("collecting", Calculate([Local(Steady(from)), server], history.OrderBy(entry => entry.At).ToList()).Burn.Accounts.Single().FiveHour.State);
    }

    [Fact]
    public void UseOfAModelWithNoPriceIsLeftOutOfAWeightedRate()
    {
        var from = End.AddHours(-2);
        // A model with no price in every other pair.
        var rows = Steady(from).Select((row, i) => i % 2 == 0 ? row : row.ToArray()).ToList();
        foreach (var (row, i) in rows.Select((row, i) => (row, i)).Where(pair => pair.i % 2 == 1)) row[1] = 1;
        var summary = Summary(rows);
        summary = new DeviceTokenUsageSummary { SchemaVersion = 7, Models = ["claude-opus-5-5", "made-up-model"], Tiers = ["default"], Minutes = summary.Minutes };
        var view = Calculate([new TokenDeviceInput("local", "이 PC", "local", true, null, summary)], Readings(from, i => 10 + i * 2.0), basis: TokenBurnBasis.Weighted);

        var five = view.Burn.Accounts.Single().FiveHour;
        Assert.Equal(12 * 2, five.DeltaPercent);
        Assert.Equal(0, five.UntrackedPercent);
        Assert.Equal(12 * 21_000_000, view.Burn.UnpricedTokens);
        Assert.Equal(2 / 2.625, five.RatePerMillion!.Value, 6);
    }

    [Fact]
    public void IdleStretchesAreNeitherRunsNorWindows()
    {
        var from = End.AddHours(-3);
        // An hour of use in one five-hour window, then two quiet hours with an unused window and a gap in the readings.
        var rows = Steady(from).Where(row => row[0] < MinuteOf(from.AddHours(1)));
        var readings = Readings(from, i => i <= 12 ? 10 + i * 2.0 : i < 24 ? 34 : 0,
            reset: i => i < 24 ? End.AddHours(2) : null).Where((_, i) => i is not (30 or 31 or 32)).ToList();
        var five = Calculate([Local(rows)], readings, hours: 3).Burn.Accounts.Single().FiveHour;

        Assert.Equal(1, five.Runs);
        Assert.Equal(1, five.Windows);
        Assert.Equal("ok", five.State);
    }

    [Fact]
    public void DevicesListTheirModelsWithinThePeriod()
    {
        var rows = new[] { Row(End.AddMinutes(-30), 1_000_000, 0, 10_000), Row(End.AddMinutes(-20), 500_000, 0, 5_000, model: 1), Row(End.AddDays(-2), 9_000_000, 0, 0) };
        var device = Calculate([Local(rows)], [], hours: 1).Devices.Single();

        Assert.Equal(["claude-opus-5-5", "claude-sonnet-5-5"], device.Models.Select(model => model.Model));
        Assert.Equal(1_515_000, device.TotalTokens);
        Assert.True(device.EstimatedCostUsd > 0);
    }

    [Fact]
    public void HistoryIsKeptAsLinesAndReadBack()
    {
        var paths = new WindowsPaths(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
        var store = new QuotaHistoryStore(paths);
        store.Append(new QuotaHistoryEntry { Kind = QuotaHistoryEntry.Switch, At = End.AddDays(-40), ProfileId = 1, DeviceIds = ["local"] });
        // A line past the retention goes when the next line is added.
        store.Append(new QuotaHistoryEntry
        {
            Kind = QuotaHistoryEntry.Poll, At = End.AddMinutes(-5),
            Samples = [new QuotaSample(1, End.AddMinutes(-5), "Max 20x", 12.5, End.AddHours(2), 40, End.AddDays(3))],
            Devices = [new("local", 1, End.AddMinutes(-5))],
        });
        Assert.DoesNotContain("\"switch\"", File.ReadAllText(store.FilePath));
        File.AppendAllText(store.FilePath, "not json\n");

        var poll = Assert.Single(store.Read(DateTimeOffset.MinValue));
        Assert.Equal(12.5, poll.Samples!.Single().FiveHourUsed);
        Assert.Equal(End.AddMinutes(-5), store.FirstSampledAt());
        store.Append(new QuotaHistoryEntry { Kind = QuotaHistoryEntry.Anchor, At = End, ProfileId = 1 });
        Assert.Equal(2, store.Read(DateTimeOffset.MinValue).Count);
        Assert.Empty(store.Read(End.AddMinutes(1)));
    }
}
