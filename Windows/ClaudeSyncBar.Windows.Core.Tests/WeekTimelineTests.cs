using System.Text.Json;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Windows.Core.Tests;

/// <summary>The week split between the accounts by share, and what the history shows beside that plan.</summary>
public sealed class WeekTimelineTests
{
    private static readonly TimeSpan Nine = TimeSpan.FromHours(9);
    // Sunday evening; the week it falls in runs from Monday the 9th 08:00 to Monday the 16th 08:00.
    private static readonly DateTimeOffset Now = Kst(11, 15, 17, 44);

    private static DateTimeOffset Kst(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, Nine);

    private static WeekTimelineAccount Account(int id, string name, DateTimeOffset? resetsAt, int share = 100, double? used = 0,
        bool available = true, bool current = false) => new(id, name, share, available, used, resetsAt, current, true);

    // The four accounts of the contract's example, in list order; 개인 2's reset is read twenty seconds early.
    private static List<WeekTimelineAccount> Canonical(Func<WeekTimelineAccount, WeekTimelineAccount>? change = null)
    {
        List<WeekTimelineAccount> accounts =
        [
            Account(1, "개인 1", Kst(11, 22, 16), used: 30),
            Account(6, "그립랩스", Kst(11, 19, 11), used: 40),
            Account(7, "이더블에스", Kst(11, 20, 15), share: 70, used: 20),
            Account(8, "개인 2", new DateTimeOffset(2026, 11, 16, 7, 59, 40, Nine), used: 5, current: true),
        ];
        return change is null ? accounts : accounts.Select(change).ToList();
    }

    private static QuotaHistoryEntry Poll(int profileId, DateTimeOffset at, double? used, DateTimeOffset? resetsAt) =>
        new() { Kind = QuotaHistoryEntry.Poll, At = at, Samples = [new QuotaSample(profileId, at, "Max 20x", used, resetsAt)] };

    private static QuotaHistoryEntry Seen(DateTimeOffset at, int? profileId, string device = "local", bool unregistered = false) =>
        new() { Kind = QuotaHistoryEntry.Poll, At = at, Devices = [new DeviceAccountObservation(device, profileId, at, unregistered)] };

    private static QuotaHistoryEntry Switch(DateTimeOffset at, int profileId, string device = "local") =>
        new() { Kind = QuotaHistoryEntry.Switch, At = at, ProfileId = profileId, DeviceIds = [device] };

    private static WeekTimelineView View(IReadOnlyList<WeekTimelineAccount> accounts, IEnumerable<QuotaHistoryEntry> history,
        int week = 0, DateTimeOffset? now = null) =>
        WeekTimelineCalculator.Calculate(new WeekTimelineRequest(now ?? Now, week, accounts, history.OrderBy(entry => entry.At).ToList()));

    private static void AssertClose(double[] expected, IEnumerable<double> actual, int precision)
    {
        var values = actual.ToArray();
        Assert.Equal(expected.Length, values.Length);
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], values[i], precision);
    }

    // The result goes over the wire as JSON, which has no NaN and no infinity: the serializer refuses them.
    private static void AssertSerializable(WeekTimelineView view) => Assert.NotEmpty(JsonSerializer.Serialize(view));

    [Fact]
    public void TheWeekIsCutByShareAndEndsWhereTheAccountWithNoRoomResets()
    {
        var view = View(Canonical(), []);

        Assert.Equal("ok", view.State);
        Assert.Equal(0, view.Week);
        Assert.Equal(370, view.TotalShare);
        Assert.Equal(370 / 7d, view.DailyShare, 9);
        Assert.Equal(Kst(11, 9, 8), view.StartsAt);
        Assert.Equal(Kst(11, 16, 8), view.EndsAt);
        Assert.Equal(Now, view.Now);
        Assert.Equal(153.7333 / 168, view.NowAt!.Value, 6);

        // 그립랩스, 이더블에스, 개인 1, then 개인 2, whose segment ends exactly at its reset.
        Assert.Equal(new[] { 6, 7, 1, 8 }, view.Segments.Select(segment => segment.ProfileId).ToArray());
        Assert.Equal(new[] { 100, 70, 100, 100 }, view.Segments.Select(segment => segment.Share).ToArray());
        Assert.Equal(view.StartsAt, view.Segments[0].StartsAt);
        Assert.Equal(view.EndsAt, view.Segments[^1].EndsAt);
        for (var i = 1; i < view.Segments.Count; i++) Assert.Equal(view.Segments[i - 1].EndsAt, view.Segments[i].StartsAt);
        AssertClose([45.4054, 77.1892, 122.5946, 168], view.Segments.Select(segment => (segment.EndsAt - view.StartsAt).TotalHours), 4);
        AssertClose([0, 0.270270, 0.459459, 0.729730], view.Segments.Select(segment => segment.At), 6);
        AssertClose([0.270270, 0.189189, 0.270270, 0.270270], view.Segments.Select(segment => segment.Width), 6);
        AssertClose([29.5946, 25.8108, 29.4054, 0], view.Segments.Select(segment => segment.SlackHours), 4);
        Assert.Equal(new[] { Kst(11, 12, 11), Kst(11, 13, 15), Kst(11, 15, 16), Kst(11, 16, 8) },
            view.Segments.Select(segment => segment.ResetsAt).ToArray());
        Assert.All(view.Segments, segment => Assert.True(segment.At + segment.Width <= 1));

        Assert.Equal(338.58, view.Summary.PlannedSoFar, 2);
        Assert.Equal(new[] { 6, 7, 1, 8 }, view.Accounts.Select(row => row.ProfileId).ToArray());
        Assert.All(view.Accounts, row => Assert.True(row.Planned && row.Excluded is null && !row.ResetProjected));
        var shared = view.Accounts[1];
        Assert.Equal(Kst(11, 20, 15), shared.ResetsAt);
        Assert.Equal(view.Segments[1].StartsAt, shared.SegmentStartsAt);
        Assert.Equal(view.Segments[1].EndsAt, shared.SegmentEndsAt);
        Assert.Equal(25.8108, shared.SlackHours!.Value, 4);
        AssertSerializable(view);
    }

    [Fact]
    public void TheTurnGoesToTheNearestResetAndForecastsWhatThePlansPaceCannotUse()
    {
        var summary = View(Canonical(), []).Summary;

        // 개인 2 has 95 left and 14 h 16 min to go: the plan's pace, 370 a week, uses 31.42 of it.
        var turn = summary.Turn!;
        Assert.Equal(8, turn.ProfileId);
        Assert.Equal("개인 2", turn.Name);
        Assert.True(turn.IsCurrent);
        Assert.Equal(Kst(11, 16, 8), turn.ResetsAt);
        Assert.Null(turn.StartsAt);
        Assert.Equal(95, turn.MineLeft!.Value, 6);
        Assert.Equal(63.58, turn.ForecastLost!.Value, 2);

        var next = summary.Next!;
        Assert.Equal(6, next.ProfileId);
        Assert.False(next.IsCurrent);
        Assert.Equal(Kst(11, 16, 8), next.StartsAt);
        Assert.Equal(Kst(11, 19, 11), next.ResetsAt);
        Assert.Equal(60, next.MineLeft!.Value, 6);
        Assert.Null(next.ForecastLost);
    }

    [Fact]
    public void TheTurnSkipsAnAccountWithLessThanOnePercentAndPrefersMoreLeftBetweenEqualResets()
    {
        var spent = View(Canonical(account => account.ProfileId == 8 ? account with { UsedPercent = 99.5 } : account), []).Summary;
        Assert.Equal(6, spent.Turn!.ProfileId);
        // 60 left with 89 hours to go at 2.2 an hour: all of it can be used.
        Assert.Equal(0, spent.Turn.ForecastLost!.Value, 6);
        Assert.Equal(7, spent.Next!.ProfileId);
        Assert.Equal(Kst(11, 19, 11), spent.Next.StartsAt);
        Assert.Equal(50, spent.Next.MineLeft!.Value, 6);

        var tied = View([Account(1, "가", Kst(11, 18, 12), used: 60), Account(2, "나", Kst(11, 18, 12), used: 20)], []).Summary;
        Assert.Equal(2, tied.Turn!.ProfileId);
        Assert.Equal(1, tied.Next!.ProfileId);

        var none = View(Canonical(account => account with { UsedPercent = 100 }), []).Summary;
        Assert.Null(none.Turn);
        Assert.Null(none.Next);
    }

    [Theory]
    [InlineData(7, 57, 30, 8, 0)]
    [InlineData(7, 59, 40, 8, 0)]
    [InlineData(8, 2, 29, 8, 0)]
    [InlineData(8, 2, 31, 8, 5)]
    public void AResetIsRoundedToTheNearestFiveMinutes(int hour, int minute, int second, int roundedHour, int roundedMinute)
    {
        var view = View([Account(8, "개인 2", new DateTimeOffset(2026, 11, 16, hour, minute, second, Nine))], []);

        var expected = Kst(11, 16, roundedHour, roundedMinute);
        Assert.Equal(expected, view.EndsAt);
        Assert.Equal(expected, view.Segments.Single().ResetsAt);
        Assert.Equal(expected, view.Accounts.Single().ResetsAt);
        Assert.Equal(expected, view.Resets.Single().ResetsAt);
    }

    [Fact]
    public void FourEqualSharesGetFortyTwoHoursEachAndTheSameAccountStillBinds()
    {
        var view = View(Canonical(account => account with { Share = 100 }), []);

        Assert.Equal(400, view.TotalShare);
        Assert.Equal(Kst(11, 16, 8), view.EndsAt);
        Assert.Equal(new[] { 6, 7, 1, 8 }, view.Segments.Select(segment => segment.ProfileId).ToArray());
        AssertClose([42, 84, 126, 168], view.Segments.Select(segment => (segment.EndsAt - view.StartsAt).TotalHours), 9);
        AssertClose([0.25, 0.25, 0.25, 0.25], view.Segments.Select(segment => segment.Width), 9);
        AssertClose([33, 19, 26, 0], view.Segments.Select(segment => segment.SlackHours), 9);
    }

    [Fact]
    public void OnePlannedAccountHasTheWholeWeekUpToItsReset()
    {
        var view = View([Account(6, "그립랩스", Kst(11, 19, 11), share: 40, used: 10)], []);

        Assert.Equal(40, view.TotalShare);
        Assert.Equal(Kst(11, 12, 11), view.StartsAt);
        Assert.Equal(Kst(11, 19, 11), view.EndsAt);
        var segment = Assert.Single(view.Segments);
        Assert.Equal((view.StartsAt, view.EndsAt), (segment.StartsAt, segment.EndsAt));
        Assert.Equal((0d, 1d, 0d), (segment.At, segment.Width, segment.SlackHours));
        // Its share, 40, is all that is the user's; 10 of it is used.
        Assert.Equal(30, view.Accounts.Single().MineLeft!.Value, 6);
        Assert.Null(view.Summary.Next);
    }

    [Fact]
    public void WithNoAccountToPlanTheViewIsEmptyAndEveryRowSaysWhy()
    {
        var view = View(
        [
            Account(1, "로그인 필요", null, share: 0, available: false),
            Account(2, "몫 없음", null, share: 0),
            Account(3, "초기화 모름", null),
            Account(4, "로그인이 먼저", Kst(11, 19, 11), available: false),
            Account(5, "몫이 먼저", Kst(11, 19, 11), share: 0),
        ], []);

        Assert.Equal("empty", view.State);
        Assert.Equal(Now - TimeSpan.FromHours(168), view.StartsAt);
        Assert.Equal(Now, view.EndsAt);
        Assert.Null(view.NowAt);
        Assert.Equal((0d, 0d), (view.TotalShare, view.DailyShare));
        Assert.Empty(view.Days);
        Assert.Empty(view.Segments);
        Assert.Empty(view.Holds);
        Assert.Empty(view.Resets);
        Assert.Equal(new WeekTimelineSummary(0, 0, 0, null, null), view.Summary);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, view.Accounts.Select(row => row.ProfileId).ToArray());
        Assert.Equal(new[] { "login", "share", "reset", "login", "share" }, view.Accounts.Select(row => row.Excluded!).ToArray());
        Assert.All(view.Accounts, row => Assert.True(!row.Planned && row.SegmentStartsAt is null && row.SegmentEndsAt is null && row.SlackHours is null));
        AssertSerializable(view);
    }

    [Fact]
    public void AccountsLeftOutOfThePlanFollowThePlannedOnesInListOrder()
    {
        var accounts = Canonical();
        accounts.Insert(0, Account(9, "로그인 필요", Kst(11, 17, 9), available: false));
        accounts.Insert(3, Account(10, "몫 없음", Kst(11, 17, 9), share: 0));
        var view = View(accounts, []);

        Assert.Equal(370, view.TotalShare);
        Assert.Equal(Kst(11, 16, 8), view.EndsAt);
        Assert.Equal(4, view.Segments.Count);
        Assert.Equal(new[] { 6, 7, 1, 8, 9, 10 }, view.Accounts.Select(row => row.ProfileId).ToArray());
        Assert.Equal(new[] { null, null, null, null, "login", "share" }, view.Accounts.Select(row => row.Excluded).ToArray());
        Assert.Equal(4, view.Resets.Count);
    }

    [Fact]
    public void AccountsThatResetTogetherKeepListOrderAndTheLaterOneBinds()
    {
        var reset = Kst(11, 18, 12);
        var view = View([Account(1, "가", reset), Account(2, "나", reset)], []);

        Assert.Equal(reset, view.EndsAt);
        Assert.Equal(new[] { 1, 2 }, view.Segments.Select(segment => segment.ProfileId).ToArray());
        AssertClose([84, 0], view.Segments.Select(segment => segment.SlackHours), 9);
        Assert.Equal(new[] { reset, reset }, view.Segments.Select(segment => segment.ResetsAt).ToArray());
        Assert.Equal(new[] { 1, 2 }, view.Resets.Select(item => item.ProfileId).ToArray());
    }

    [Fact]
    public void WhenTwoAccountsHaveTheSameRoomTheEarlierOneEndsTheWeek()
    {
        // Half a week each, resetting half a week apart: both segments end exactly at their resets.
        var view = View([Account(1, "가", Kst(11, 18, 12)), Account(2, "나", Kst(11, 22, 0))], []);

        Assert.Equal(Kst(11, 18, 12), view.EndsAt);
        Assert.Equal(new[] { 2, 1 }, view.Segments.Select(segment => segment.ProfileId).ToArray());
        AssertClose([0, 0], view.Segments.Select(segment => segment.SlackHours), 9);
        Assert.Equal(new[] { Kst(11, 15, 0), Kst(11, 18, 12) }, view.Segments.Select(segment => segment.ResetsAt).ToArray());
    }

    [Fact]
    public void AResetAlreadyPastIsProjectedAWeekOnAndComesLastForTheTurn()
    {
        // 가's reset passed yesterday and was not read again: its window lapsed, and the next reset is a guess.
        var view = View([Account(1, "가", Kst(11, 14, 10), used: 80), Account(2, "나", Kst(11, 18, 12), used: 50)], []);

        Assert.Equal(Kst(11, 14, 10), view.StartsAt);
        Assert.Equal(Kst(11, 21, 10), view.EndsAt);
        Assert.Equal(new[] { 2, 1 }, view.Segments.Select(segment => segment.ProfileId).ToArray());
        AssertClose([14, 0], view.Segments.Select(segment => segment.SlackHours), 9);

        var row = view.Accounts.Single(item => item.ProfileId == 1);
        Assert.True(row.Planned);
        Assert.True(row.ResetProjected);
        Assert.Equal(Kst(11, 21, 10), row.ResetsAt);
        Assert.Equal((100d, 0d, 100d), (row.Remaining!.Value, row.MineUsed!.Value, row.MineLeft!.Value));
        Assert.False(view.Accounts.Single(item => item.ProfileId == 2).ResetProjected);

        Assert.Equal(new[] { (2, true, false), (1, true, true) }, view.Resets.Select(item => (item.ProfileId, item.Upcoming, item.Projected)).ToArray());
        Assert.All(view.Resets, item => Assert.True(item.Left is null && item.Over is null && !item.Spent));

        Assert.Equal(2, view.Summary.Turn!.ProfileId);
        var next = view.Summary.Next!;
        Assert.Equal(1, next.ProfileId);
        Assert.Null(next.ResetsAt);
        Assert.Equal(Kst(11, 18, 12), next.StartsAt);

        // Alone in the queue, the projected account has the turn, with no reset to forecast from.
        var alone = View([Account(1, "가", Kst(11, 14, 10), used: 80), Account(2, "나", Kst(11, 18, 12), used: 100)], []).Summary;
        Assert.Equal(1, alone.Turn!.ProfileId);
        Assert.Null(alone.Turn.ResetsAt);
        Assert.Null(alone.Turn.ForecastLost);
        Assert.Equal(100, alone.Turn.MineLeft!.Value, 6);
        Assert.Null(alone.Next);

        // Seen from the week before, both resets are behind: neither is ahead, and a reset that has happened is no guess.
        var earlier = View([Account(1, "가", Kst(11, 14, 10), used: 80), Account(2, "나", Kst(11, 18, 12), used: 50)], [], week: -1);
        Assert.Equal(new[] { (2, Kst(11, 11, 12), false, false), (1, Kst(11, 14, 10), false, false) },
            earlier.Resets.Select(item => (item.ProfileId, item.ResetsAt, item.Upcoming, item.Projected)).ToArray());
    }

    [Fact]
    public void AnAccountWithNoResetReadTakesItsPhaseFromTheNewestReadingThatNamedOne()
    {
        List<WeekTimelineAccount> accounts = [Account(3, "쉬는 계정", null, used: 0)];
        Assert.Equal("reset", View(accounts, []).Accounts.Single().Excluded);

        var history = new[]
        {
            Poll(3, Kst(11, 5, 9), 20, Kst(11, 2, 12).AddDays(7)),
            Poll(3, Kst(11, 8, 9), 40, new DateTimeOffset(2026, 11, 9, 11, 59, 30, Nine)),
            Poll(3, Kst(11, 10, 9), 0, null),
        };
        var view = View(accounts, history);

        Assert.Equal("ok", view.State);
        Assert.Equal(Kst(11, 16, 12), view.EndsAt);
        var row = view.Accounts.Single();
        Assert.True(row.Planned);
        Assert.True(row.ResetProjected);
        Assert.Equal(Kst(11, 16, 12), row.ResetsAt);
        Assert.Equal(100, row.MineLeft!.Value, 6);
        var reset = view.Resets.Single();
        Assert.True(reset.Upcoming && reset.Projected);
        Assert.Null(view.Summary.Turn!.ResetsAt);
    }

    [Fact]
    public void DaysCarryThePlansDailyShareAndADayNotBegunHasNoUse()
    {
        // Tuesday noon of the same week: the resets asked for next week are the ones still ahead in this one.
        var view = View(Canonical(), [], now: Kst(11, 10, 12));

        Assert.Equal(Kst(11, 9, 8), view.StartsAt);
        Assert.Equal(Kst(11, 16, 8), view.EndsAt);
        Assert.Equal(7, view.Days.Count);
        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(Kst(11, 9 + i, 8), view.Days[i].StartsAt);
            Assert.Equal(Kst(11, 10 + i, 8), view.Days[i].EndsAt);
            Assert.Equal(370 / 7d, view.Days[i].Planned, 9);
            Assert.Equal(i == 1, view.Days[i].IsToday);
            Assert.Equal(i <= 1 ? 0d : (double?)null, view.Days[i].Used);
        }

        Assert.Equal(28d / 168, view.NowAt!.Value, 9);
        Assert.Equal(370 * 28d / 168, view.Summary.PlannedSoFar, 6);
        Assert.Equal(new[] { true, true, true, true }, view.Resets.Select(reset => reset.Upcoming).ToArray());
    }

    [Fact]
    public void AShortVisitToAnotherAccountDoesNotSplitALongHold()
    {
        var reset = Kst(11, 15, 16);
        var view = View(Canonical(),
        [
            Poll(1, Kst(11, 10, 8, 55), 20, reset),
            Seen(Kst(11, 10, 9), 1),
            Switch(Kst(11, 10, 12), 6),
            Switch(Kst(11, 10, 12, 10), 1),
            Poll(1, Kst(11, 10, 17, 58), 30, reset),
            // Twenty minutes on 그립랩스, then an hour nobody knows which account was on.
            Switch(Kst(11, 10, 18), 6),
            Seen(Kst(11, 10, 18, 20), 6),
            Seen(Kst(11, 10, 19), 1),
        ]);

        Assert.Equal(3, view.Holds.Count);
        Assert.All(view.Holds, hold => Assert.Equal((1, "개인 1"), (hold.ProfileId, hold.Name)));
        var (first, until, open) = (view.Holds[0], view.Holds[1], view.Holds[2]);
        Assert.Equal((Kst(11, 10, 9), Kst(11, 10, 18)), (first.StartsAt, first.EndsAt));
        Assert.Equal(25d / 168, first.At, 9);
        Assert.Equal(9d / 168, first.Width, 9);
        Assert.Equal((80d, 70d), (first.RemainingFrom!.Value, first.RemainingTo!.Value));
        Assert.False(first.IsOpen);

        // The last look still holds and runs to now, across the reset at four: the window it began in and the
        // one after it are two holds.
        Assert.Equal((Kst(11, 10, 19), reset), (until.StartsAt, until.EndsAt));
        Assert.Equal((70d, 70d), (until.RemainingFrom!.Value, until.RemainingTo!.Value));
        Assert.False(until.IsOpen);
        Assert.Equal((reset, Now), (open.StartsAt, open.EndsAt));
        Assert.Equal((100d, 100d), (open.RemainingFrom!.Value, open.RemainingTo!.Value));
        Assert.True(open.IsOpen);
        Assert.All(view.Holds, hold => Assert.True(hold.At + hold.Width <= 1));
    }

    [Fact]
    public void HoldsAreCutToTheWeekAndLeaveOutLoginsThatAreNotInTheList()
    {
        QuotaHistoryEntry[] history =
        [
            Seen(Kst(11, 8, 20), 6),
            Seen(Kst(11, 9, 10), 6),
            Seen(Kst(11, 9, 12), null, unregistered: true),
            Seen(Kst(11, 9, 14), null, unregistered: true),
            Switch(Kst(11, 9, 14, 30), 99),
            Switch(Kst(11, 9, 16), 6),
            Seen(Kst(11, 9, 20), 6),
            Seen(Kst(11, 9, 20, 5), null),
        ];
        var view = View(Canonical(), history);

        // Twelve hours before the week began are cut off; the unregistered login and the deleted account leave a gap.
        Assert.Equal(new[] { (6, Kst(11, 9, 8), Kst(11, 9, 10)), (6, Kst(11, 9, 16), Kst(11, 9, 20)) },
            view.Holds.Select(hold => (hold.ProfileId, hold.StartsAt, hold.EndsAt)).ToArray());
        Assert.Equal(0, view.Holds[0].At);
        Assert.Equal(2d / 168, view.Holds[0].Width, 9);
        Assert.All(view.Holds, hold => Assert.True(!hold.IsOpen && hold.RemainingFrom is null && hold.RemainingTo is null));

        // The week before ends where this one begins, long before now: the first hold is cut there, and the rest are after it.
        var before = Assert.Single(View(Canonical(), history, week: -1).Holds);
        Assert.Equal((6, Kst(11, 8, 20), Kst(11, 9, 8), false), (before.ProfileId, before.StartsAt, before.EndsAt, before.IsOpen));
        Assert.Equal(156d / 168, before.At, 9);
        Assert.Equal(12d / 168, before.Width, 9);
    }

    [Fact]
    public void AHoldIsOpenWhileItReachesNowAndLooksJoinIntoOneHold()
    {
        IEnumerable<QuotaHistoryEntry> Looks(int minutesBeforeNow)
        {
            for (var at = Now.AddHours(-3); at <= Now.AddMinutes(-minutesBeforeNow); at += TimeSpan.FromMinutes(5)) yield return Seen(at, 8);
            yield return Seen(Now.AddMinutes(-minutesBeforeNow + 1), null);
        }

        var open = Assert.Single(View(Canonical(), Looks(10)).Holds);
        Assert.Equal((Now.AddHours(-3), Now.AddMinutes(-10)), (open.StartsAt, open.EndsAt));
        Assert.True(open.IsOpen);

        Assert.False(Assert.Single(View(Canonical(), Looks(20)).Holds).IsOpen);
        // Twenty-five minutes on an account that then went out of sight is too short to show.
        Assert.Empty(View(Canonical(), [Seen(Now.AddMinutes(-120), 8), Seen(Now.AddMinutes(-95), 8), Seen(Now.AddMinutes(-90), null)]).Holds);
    }

    [Fact]
    public void AHoldAcrossItsAccountsResetEndsThereWithWhatTheResetFound()
    {
        var reset = Kst(11, 15, 16);
        var view = View(Canonical(),
        [
            Seen(Kst(11, 12, 0, 23), 1),
            Poll(1, Kst(11, 12, 0, 28), 1, reset),
            Poll(1, Kst(11, 15, 15, 37), 96, reset),
            // The switch comes two minutes after the reset, as when the user moves on once an account turns over.
            Poll(1, Kst(11, 15, 16, 1), 0, null),
            Switch(Kst(11, 15, 16, 2), 8),
        ]);

        // Not "100 → 100%" across the reset: the hold ends at it with the 4% it found, and the two minutes after
        // it are too short to show.
        Assert.Equal(new[] { (1, Kst(11, 12, 0, 23), reset, 99d, 4d, false), (8, Kst(11, 15, 16, 2), Now, (double?)null, (double?)null, true) }
                .Select(hold => (hold.Item1, hold.Item2, hold.Item3, (double?)hold.Item4, (double?)hold.Item5, hold.Item6)).ToArray(),
            view.Holds.Select(hold => (hold.ProfileId, hold.StartsAt, hold.EndsAt, hold.RemainingFrom, hold.RemainingTo, hold.IsOpen)).ToArray());
    }

    [Fact]
    public void TheAccountJustSwitchedToShowsAtOnceAndOnlyItIsOpen()
    {
        var holds = View(Canonical(), [Seen(Now.AddHours(-5), 1), Switch(Now.AddMinutes(-8), 8)]).Holds;

        // Eight minutes are less than a hold needs to show, but this one reaches now: it is the account in use.
        Assert.Equal(new[] { (1, Now.AddHours(-5), Now.AddMinutes(-8), false), (8, Now.AddMinutes(-8), Now, true) },
            holds.Select(hold => (hold.ProfileId, hold.StartsAt, hold.EndsAt, hold.IsOpen)).ToArray());
        // The same eight minutes in an earlier week's view, or long over, do not show.
        Assert.DoesNotContain(View(Canonical(), [Seen(Now.AddHours(-5), 1), Switch(Now.AddMinutes(-8), 8)], week: -1).Holds, hold => hold.ProfileId == 8);
    }

    [Fact]
    public void WhatWasLeftBeforeTheFirstReadingComesFromOneWithinHalfAnHour()
    {
        QuotaHistoryEntry[] Looks(int readingMinute) =>
        [
            Seen(Kst(11, 10, 9), 6), Seen(Kst(11, 10, 12), 6), Seen(Kst(11, 10, 12, 5), null),
            Poll(6, Kst(11, 10, 9, readingMinute), 20, Kst(11, 12, 11)),
        ];

        var near = Assert.Single(View(Canonical(), Looks(20)).Holds);
        Assert.Equal((80d, 80d), (near.RemainingFrom!.Value, near.RemainingTo!.Value));

        var far = Assert.Single(View(Canonical(), Looks(40)).Holds);
        Assert.Null(far.RemainingFrom);
        Assert.Equal(80, far.RemainingTo!.Value, 6);
    }

    [Fact]
    public void ARiseInsideOneWindowIsUseOnTheDayItHappened()
    {
        var reset = Kst(11, 15, 16);
        var view = View(Canonical(), [Poll(1, Kst(11, 10, 10), 10, reset), Poll(1, Kst(11, 10, 10, 5), 12, reset)]);

        AssertClose([0, 2, 0, 0, 0, 0, 0], view.Days.Select(day => day.Used!.Value), 9);
        Assert.Equal(2, view.Summary.Used, 9);
    }

    [Fact]
    public void AfterAResetAllTheNewWindowShowsIsUseSinceTheReset()
    {
        var view = View(Canonical(),
        [
            Poll(6, Kst(11, 12, 10, 55), 80, Kst(11, 12, 11)),
            Poll(6, Kst(11, 12, 11, 5), 3, Kst(11, 19, 11, 3)),
            // 개인 2 resets where the week begins: the hour before it belongs to the week before.
            Poll(8, Kst(11, 9, 7), 90, Kst(11, 9, 8)),
            Poll(8, Kst(11, 9, 9), 4, Kst(11, 16, 8)),
        ]);

        AssertClose([4, 0, 0, 3, 0, 0, 0], view.Days.Select(day => day.Used!.Value), 9);
        Assert.Equal(7, view.Summary.Used, 9);
        Assert.Equal(0, View(Canonical(), [Poll(8, Kst(11, 9, 7), 90, Kst(11, 9, 8)), Poll(8, Kst(11, 9, 9), 4, Kst(11, 16, 8))], week: -1).Summary.Used, 9);
    }

    [Fact]
    public void AReadingLowerThanTheOneBeforeIsLeftOutAndTheNextIsComparedAcrossIt()
    {
        var reset = Kst(11, 15, 16);
        var view = View(Canonical(),
        [
            Poll(1, Kst(11, 10, 10), 10, reset), Poll(1, Kst(11, 10, 10, 5), 5, reset),
            Poll(1, Kst(11, 10, 10, 10), 12, reset), Poll(1, Kst(11, 10, 10, 15), 13, reset),
        ]);

        // The 5 is a wrong reading: the use went from 10 to 12 and then 13, and the climb back from 5 is none.
        Assert.Equal(3, view.Summary.Used, 9);
        Assert.All(view.Days, day => Assert.True(day.Used >= 0));

        // What a window's readings count is what they rose by from first to last: 50, 48, 53, 55 is 5. The rise
        // of 3 lies between the readings of 50 at 07:00 and 53 at 09:00, half of it before the day began at 08:00.
        var hourly = View(Canonical(),
        [
            Poll(1, Kst(11, 10, 7), 50, reset), Poll(1, Kst(11, 10, 8), 48, reset),
            Poll(1, Kst(11, 10, 9), 53, reset), Poll(1, Kst(11, 10, 10), 55, reset),
        ]);
        AssertClose([1.5, 3.5, 0, 0, 0, 0, 0], hourly.Days.Select(day => day.Used!.Value), 9);
        Assert.Equal(5, hourly.Summary.Used, 9);
    }

    [Fact]
    public void AReadingTakenAfterTheResetItNamesIsStillOfTheWindowThatEnded()
    {
        // This PC's clock ran ten seconds ahead of the service: the last look before 개인 1's reset came after it.
        var early = View(Canonical(),
            [Poll(1, Kst(11, 15, 15, 55), 90, Kst(11, 15, 16)), Poll(1, Kst(11, 15, 16).AddSeconds(10), 90, Kst(11, 15, 16))]);
        Assert.Equal(0, early.Days[6].Used!.Value, 9);
        Assert.Equal(0, early.Summary.Used, 9);

        // An account that went on reading its old window for twenty minutes after the reset, then 3 of the new one.
        static QuotaHistoryEntry[] Stale(int id, DateTimeOffset reset) =>
        [
            Poll(id, reset.AddMinutes(-20), 94, reset), Poll(id, reset.AddMinutes(-10), 94, reset),
            Poll(id, reset.AddMinutes(1), 94, reset), Poll(id, reset.AddMinutes(11), 94, reset),
            Poll(id, reset.AddMinutes(21), 0, reset.AddDays(7)), Poll(id, reset.AddHours(2), 3, reset.AddDays(7)),
        ];

        var whole = View(Canonical(), Stale(6, Kst(11, 12, 11)));
        AssertClose([0, 0, 0, 3, 0, 0, 0], whole.Days.Select(day => day.Used!.Value), 9);
        Assert.Equal(6, whole.Resets.Single(item => item.ProfileId == 6).Left!.Value, 9);

        // On the shared account nobody of the user's was on it: only the 3 are other people's use of the running window.
        var shared = View(Canonical(), Stale(7, Kst(11, 13, 15)));
        var row = shared.Accounts.Single(item => item.ProfileId == 7);
        Assert.Equal((17d, 53d), (Math.Round(row.MineUsed!.Value, 9), Math.Round(row.MineLeft!.Value, 9)));
        var ended = shared.Resets.Single(item => item.ProfileId == 7);
        Assert.Equal((0d, 24d), (Math.Round(ended.Left!.Value, 9), Math.Round(ended.Over!.Value, 9)));
        Assert.Equal(0, shared.Summary.Used, 9);
    }

    [Fact]
    public void AReadingThatShowsUseButNamesNoResetTellsNoWindowAndIsReadAcross()
    {
        var reset = Kst(11, 15, 16);
        var view = View(Canonical(),
            [Poll(1, Kst(11, 10, 10), 50, reset), Poll(1, Kst(11, 10, 10, 5), 60, null), Poll(1, Kst(11, 10, 10, 10), 62, reset)]);

        // From 50 to 62 in one window; the 60 in between is neither a new window's use nor the start of one.
        AssertClose([0, 12, 0, 0, 0, 0, 0], view.Days.Select(day => day.Used!.Value), 9);

        // An idle window names no reset and reads 0: that one does begin the window the next reading is of.
        var idle = View(Canonical(),
            [Poll(1, Kst(11, 10, 10), 50, Kst(11, 8, 16)), Poll(1, Kst(11, 10, 10, 5), 0, null), Poll(1, Kst(11, 10, 10, 10), 4, reset)]);
        AssertClose([0, 4, 0, 0, 0, 0, 0], idle.Days.Select(day => day.Used!.Value), 9);
    }

    [Fact]
    public void ANewWindowsUseBeginsNoEarlierThanTheWindowHoweverOldTheReadingBefore()
    {
        // Nothing read 그립랩스 for thirteen days. The 30 its new window shows were used since that window began,
        // on the 12th at 11:00: 47 hours up to the reading, none of them in the week before.
        QuotaHistoryEntry[] history = [Poll(6, Kst(11, 1, 10), 50, Kst(11, 5, 11)), Poll(6, Kst(11, 14, 10), 30, Kst(11, 19, 11))];
        var view = View(Canonical(), history);
        AssertClose([0, 0, 0, 30 * 21d / 47, 30 * 24d / 47, 30 * 2d / 47, 0], view.Days.Select(day => day.Used!.Value), 9);
        Assert.Equal(0, View(Canonical(), history, week: -1).Summary.Used, 9);

        // The same gap on the shared account, with no device of the user's on it: all 30 are other people's use
        // of the window that is running, so none of what it reads now is the user's.
        var shared = View(Canonical(account => account.ProfileId == 7 ? account with { UsedPercent = 30 } : account),
            [Poll(7, Kst(11, 1, 10), 50, Kst(11, 6, 15)), Poll(7, Kst(11, 14, 10), 30, Kst(11, 20, 15))]);
        var row = shared.Accounts.Single(item => item.ProfileId == 7);
        Assert.Equal((0d, 70d), (Math.Round(row.MineUsed!.Value, 9), Math.Round(row.MineLeft!.Value, 9)));
    }

    [Fact]
    public void AResetNamedAFewSecondsApartFromLookToLookIsOneReset()
    {
        // 그립랩스's reset of 11:00 is named twenty seconds early at one look and twenty seconds late at the next.
        var view = View(Canonical(),
        [
            Poll(6, Kst(11, 12, 10, 50), 55, Kst(11, 12, 11).AddSeconds(-20)),
            Poll(6, Kst(11, 12, 10, 55), 60, Kst(11, 12, 11).AddSeconds(20)),
        ]);
        AssertClose([0, 0, 0, 5, 0, 0, 0], view.Days.Select(day => day.Used!.Value), 9);
        var reset = view.Resets.Single(item => item.ProfileId == 6);
        Assert.Equal(Kst(11, 12, 11), reset.ResetsAt);
        Assert.Equal(40, reset.Left!.Value, 9);

        // Up to two minutes apart two readings are of one window; further apart a new window began, and all it shows is use.
        static double Used(int seconds) => View(Canonical(),
            [Poll(1, Kst(11, 10, 10), 10, Kst(11, 15, 16)), Poll(1, Kst(11, 10, 10, 5), 12, Kst(11, 15, 16).AddSeconds(seconds))]).Summary.Used;
        Assert.Equal(2, Used(110), 9);
        Assert.Equal(2, Used(-110), 9);
        Assert.Equal(12, Used(130), 9);
        Assert.Equal(12, Used(-130), 9);

        // A reading tells what a reset took only when the reset it names is within ten minutes of it.
        static double? Left(int minute) =>
            View(Canonical(), [Poll(6, Kst(11, 12, 10, 55), 60, Kst(11, 12, 11, minute))]).Resets.Single(item => item.ProfileId == 6).Left;
        Assert.Equal(40d, Left(9));
        Assert.Null(Left(11));
    }

    [Fact]
    public void UseReadAtTheVeryInstantOfAResetCountsAtThatInstant()
    {
        // 개인 2 resets where the week begins and was read at that instant: the 4 it shows belong to 08:00, which
        // is the last instant of the week before and not yet in this one.
        QuotaHistoryEntry[] history = [Poll(8, Kst(11, 9, 7), 90, Kst(11, 9, 8)), Poll(8, Kst(11, 9, 8), 4, Kst(11, 16, 8))];
        Assert.Equal(0, View(Canonical(), history).Summary.Used, 9);
        AssertClose([0, 0, 0, 0, 0, 0, 4], View(Canonical(), history, week: -1).Days.Select(day => day.Used!.Value), 9);

        // On the shared account such use is the user's when a device was on the account at that instant: one found
        // there by a look at it was, one that left at it was not.
        var reset = Kst(11, 13, 15);
        QuotaHistoryEntry[] readings = [Poll(7, reset.AddHours(-1), 60, reset), Poll(7, reset, 4, reset.AddDays(7))];
        var arrived = View(Canonical(), [.. readings, Seen(reset, 7, "ssh:box"), Seen(reset.AddHours(1), 7, "ssh:box")]);
        AssertClose([0, 0, 0, 0, 4, 0, 0], arrived.Days.Select(day => day.Used!.Value), 9);
        var left = View(Canonical(), [.. readings, Seen(reset.AddHours(-2), 7, "ssh:box"), Switch(reset, 6, "ssh:box")]);
        Assert.Equal(0, left.Summary.Used, 9);
    }

    [Fact]
    public void ADeviceIsOnASharedAccountFromTheLookThatFindsItThereUpToNotAtTheOneThatEndsIt()
    {
        var reset = Kst(11, 20, 15);
        QuotaHistoryEntry[] readings =
            [Poll(7, Now.AddHours(-3), 10, reset), Poll(7, Now.AddHours(-2), 20, reset), Poll(7, Now.AddHours(-1), 30, reset)];
        var accounts = Canonical(account => account.ProfileId == 7 ? account with { UsedPercent = 30 } : account);
        static (double Today, double MineUsed, double MineLeft) Told(WeekTimelineView view) =>
            (Math.Round(view.Days[6].Used!.Value, 9), Math.Round(view.Accounts.Single(row => row.ProfileId == 7).MineUsed!.Value, 9),
                Math.Round(view.Accounts.Single(row => row.ProfileId == 7).MineLeft!.Value, 9));

        // An SSH device is first found on the account at the instant of the middle reading, which is a moment of
        // the first rise as well as of the second: both are the user's.
        var found = View(accounts, [.. readings, Seen(Now.AddHours(-2), 7, "ssh:box"), Seen(Now.AddHours(-1), 7, "ssh:box")]);
        Assert.Equal((20d, 30d, 40d), Told(found));

        // A device moved off the account at that instant was on it during the first rise and at no moment of the second.
        var moved = View(accounts, [.. readings, Seen(Now.AddHours(-4), 7, "ssh:box"), Switch(Now.AddHours(-2), 6, "ssh:box")]);
        Assert.Equal((10d, 20d, 50d), Told(moved));
    }

    [Fact]
    public void BetweenAccountsWhoseResetsAreOnlyProjectedTheOneWithMoreLeftHasTheTurn()
    {
        // As the smart switch takes them: both windows have lapsed, so neither reset is known and what is left decides.
        var summary = View(
        [
            Account(1, "가", Kst(11, 10, 12), share: 30, used: 60), Account(2, "나", Kst(11, 13, 12), used: 10),
            Account(3, "다", Kst(11, 16, 12), used: 100),
        ], []).Summary;

        Assert.Equal((2, 100d), (summary.Turn!.ProfileId, summary.Turn.MineLeft!.Value));
        Assert.True(summary.Turn.ResetsAt is null && summary.Turn.ForecastLost is null);
        Assert.Equal((1, 30d), (summary.Next!.ProfileId, summary.Next.MineLeft!.Value));
        Assert.True(summary.Next.ResetsAt is null && summary.Next.StartsAt is null);

        // With as much left on both, the list's order decides, not which projected reset would come sooner.
        var level = View([Account(1, "가", Kst(11, 13, 12), used: 10), Account(2, "나", Kst(11, 10, 12), used: 60)], []).Summary;
        Assert.Equal((1, 2), (level.Turn!.ProfileId, level.Next!.ProfileId));
    }

    [Fact]
    public void WithNoResetReadTheRunningWindowIsTheOneTheHistoryTells()
    {
        var reset = Kst(11, 17, 18);
        var view = View(Canonical(account => account.ProfileId == 7 ? account with { UsedPercent = 40, ResetsAt = null } : account),
        [
            // 25 rose with no device of the user's on the account, 5 with this PC on it.
            Poll(7, Now.AddHours(-5), 10, reset), Poll(7, Now.AddHours(-4), 35, reset),
            Poll(7, Now.AddHours(-3), 35, reset), Seen(Now.AddHours(-3), 7),
            Poll(7, Now.AddHours(-2), 40, reset), Seen(Now.AddHours(-2), 7),
        ]);

        var row = view.Accounts.Single(item => item.ProfileId == 7);
        Assert.True(row.Planned && row.ResetProjected);
        Assert.Equal(reset, row.ResetsAt);
        // The row leaves out the same 25 the day's use does.
        Assert.Equal((60d, 15d, 55d), (row.Remaining!.Value, Math.Round(row.MineUsed!.Value, 9), Math.Round(row.MineLeft!.Value, 9)));
        Assert.Equal(5, view.Days.Single(day => day.IsToday).Used!.Value, 9);

        // With no reset in the history either there is no window to look in: all that is read counts as the user's.
        var unknown = View([Account(7, "이더블에스", null, share: 70, used: 40)], [Poll(7, Now.AddHours(-5), 10, null), Poll(7, Now.AddHours(-4), 35, null)]);
        var alone = unknown.Accounts.Single();
        Assert.Equal(("reset", 40d, 30d), (alone.Excluded, alone.MineUsed!.Value, alone.MineLeft!.Value));
    }

    [Fact]
    public void TheUsersPartIsNeverLessThanNothingAndNeverMoreThanTheShare()
    {
        var reset = Kst(11, 13, 15);
        var view = View(Canonical(account => account.ProfileId == 1 ? account with { UsedPercent = 250 } : account),
        [
            // Other people used 35 of 이더블에스, and the last reading before its reset shows less than that.
            Poll(7, Kst(11, 10, 10), 5, reset), Poll(7, Kst(11, 10, 11), 40, reset), Poll(7, Kst(11, 13, 14, 55), 10, reset),
            // In the window that is running they used 30, though the account is read at 20 now.
            Poll(7, Kst(11, 14, 10), 0, null), Poll(7, Kst(11, 14, 11), 30, Kst(11, 20, 15)),
        ]);

        var ended = view.Resets.Single(item => item.ProfileId == 7);
        Assert.Equal((70d, 0d), (ended.Left!.Value, ended.Over!.Value));
        var row = view.Accounts.Single(item => item.ProfileId == 7);
        Assert.Equal((80d, 0d, 70d), (row.Remaining!.Value, row.MineUsed!.Value, row.MineLeft!.Value));
        // More than the whole account cannot have been used.
        var over = view.Accounts.Single(item => item.ProfileId == 1);
        Assert.Equal((0d, 100d, 0d), (over.Remaining!.Value, over.MineUsed!.Value, over.MineLeft!.Value));
        AssertSerializable(view);
    }

    [Fact]
    public void ValuesOutOfAllRangeNeitherStopTheTimelineNorLeaveNumbersThatCannotBeWritten()
    {
        // The end of the calendar cannot be rounded up to the next five minutes, nor its start down below it.
        var first = new DateTimeOffset(1, 1, 1, 0, 0, 0, -new TimeSpan(13, 1, 0));
        var ends = View(
            Canonical(account => account.ProfileId switch
            {
                1 => account with { ResetsAt = first },
                6 => account with { ResetsAt = DateTimeOffset.MaxValue },
                _ => account,
            }),
            [
                Poll(1, Kst(11, 10, 10), 10, DateTimeOffset.MaxValue), Poll(1, Kst(11, 10, 10, 5), 12, DateTimeOffset.MaxValue),
                Poll(8, Kst(11, 10, 10), 0, DateTimeOffset.MinValue), Poll(8, Kst(11, 10, 10, 5), 3, DateTimeOffset.MaxValue),
                Poll(7, Kst(11, 10, 10), 1, first), Poll(7, Kst(11, 10, 10, 5), 2, first),
            ]);
        Assert.Equal("ok", ends.State);
        Assert.Equal(4, ends.Segments.Count);
        AssertSerializable(ends);

        // Shares beyond any percent still add up.
        var huge = View([Account(1, "가", Kst(11, 18, 12), share: int.MaxValue), Account(2, "나", Kst(11, 22, 0), share: int.MaxValue)], []);
        Assert.Equal(2d * int.MaxValue, huge.TotalShare);
        AssertClose([0.5, 0.5], huge.Segments.Select(segment => segment.Width), 9);
        AssertSerializable(huge);

        // A reading is of one account's week: never less than nothing of it and never more than all of it.
        var reset = Kst(11, 15, 16);
        var wild = View(Canonical(),
        [
            Poll(1, Kst(11, 10, 10), -1e308, reset), Poll(1, Kst(11, 10, 10, 5), 1e308, reset),
            Poll(6, Kst(11, 11, 10), 20, Kst(11, 12, 11)), Poll(6, Kst(11, 11, 10, 5), 250, Kst(11, 12, 11)),
            Seen(Kst(11, 11, 10), 6), Seen(Kst(11, 11, 12), 6), Seen(Kst(11, 11, 12, 5), null),
        ]);
        AssertClose([0, 100, 80, 0, 0, 0, 0], wild.Days.Select(day => day.Used!.Value), 9);
        var hold = Assert.Single(wild.Holds);
        Assert.Equal((80d, 0d), (hold.RemainingFrom!.Value, hold.RemainingTo!.Value));
        Assert.True(wild.Resets.Single(item => item.ProfileId == 6) is { Left: 0d, Spent: true });
        AssertSerializable(wild);
    }

    [Fact]
    public void TheHistoryMayComeInAnyOrder()
    {
        var shared = Kst(11, 13, 15);
        List<QuotaHistoryEntry> history =
        [
            Poll(6, Kst(11, 12, 10, 55), 60, Kst(11, 12, 11)),
            Poll(7, Kst(11, 11, 10), 40, shared), Poll(7, Kst(11, 11, 10, 5), 50, shared),
            Poll(7, Kst(11, 11, 12), 50, shared), Poll(7, Kst(11, 11, 13), 90, shared),
            Seen(Kst(11, 11, 12), 7), Seen(Kst(11, 11, 13), 7), Switch(Kst(11, 11, 13, 30), 1),
            Poll(1, Kst(11, 11, 13, 40), 10, Kst(11, 15, 16)), Poll(1, Kst(11, 11, 14, 40), 14, Kst(11, 15, 16)),
            Seen(Kst(11, 11, 15), 1), Seen(Kst(11, 11, 15, 5), null),
        ];
        var inOrder = WeekTimelineCalculator.Calculate(new WeekTimelineRequest(Now, 0, Canonical(), history.OrderBy(entry => entry.At).ToList()));
        Assert.Equal(new[] { (7, Kst(11, 11, 12), Kst(11, 11, 13, 30)), (1, Kst(11, 11, 13, 30), Kst(11, 11, 15)) },
            inOrder.Holds.Select(hold => (hold.ProfileId, hold.StartsAt, hold.EndsAt)).ToArray());
        Assert.Equal(44, inOrder.Summary.Used, 9);
        // 그립랩스 left 40 of its 100 unused and 개인 1 86; 이더블에스 was used beyond the share.
        Assert.Equal(126, inOrder.Summary.Lost, 9);

        // The calculator puts the readings and the looks in time order itself.
        var expected = JsonSerializer.Serialize(inOrder);
        history.Reverse();
        Assert.Equal(expected, JsonSerializer.Serialize(WeekTimelineCalculator.Calculate(new WeekTimelineRequest(Now, 0, Canonical(), history))));
        (history[2], history[7]) = (history[7], history[2]);
        (history[0], history[5]) = (history[5], history[0]);
        Assert.Equal(expected, JsonSerializer.Serialize(WeekTimelineCalculator.Calculate(new WeekTimelineRequest(Now, 0, Canonical(), history))));
    }

    [Fact]
    public void ARiseAcrossALongGapIsSpreadOverTheDaysBetween()
    {
        var reset = Kst(11, 15, 16);
        var view = View(Canonical(), [Poll(1, Kst(11, 10, 8), 10, reset), Poll(1, Kst(11, 12, 8), 34, reset)]);

        AssertClose([0, 12, 12, 0, 0, 0, 0], view.Days.Select(day => day.Used!.Value), 9);

        // Seen from the middle of the gap, only the part up to now is counted.
        var midway = View(Canonical(), [Poll(1, Kst(11, 10, 8), 10, reset), Poll(1, Kst(11, 12, 8), 34, reset)], now: Kst(11, 11, 20));
        Assert.Equal(new double?[] { 0, 12, 6, null, null, null, null }, midway.Days.Select(day => day.Used is { } used ? Math.Round(used, 9) : (double?)null).ToArray());
    }

    [Fact]
    public void ASharedAccountsRiseIsOtherPeoplesUnlessARegisteredDeviceHeldIt()
    {
        var reset = Kst(11, 13, 15);
        List<QuotaHistoryEntry> history =
        [
            Poll(7, Kst(11, 10, 10), 10, reset), Poll(7, Kst(11, 10, 11), 30, reset),
            Poll(7, Kst(11, 10, 12), 30, reset), Poll(7, Kst(11, 10, 13), 45, reset),
            Seen(Kst(11, 10, 12), 7), Seen(Kst(11, 10, 13), 7), Seen(Kst(11, 10, 13, 5), null),
        ];
        var view = View(Canonical(), history);

        // 20 rose while no device of the user's was on the account, 15 while this PC was.
        AssertClose([0, 15, 0, 0, 0, 0, 0], view.Days.Select(day => day.Used!.Value), 9);
        var reading = view.Resets.Single(item => item.ProfileId == 7);
        Assert.Equal(reset, reading.ResetsAt);
        // Of the 45 used, 25 were the user's: 45 of the share of 70 went unused.
        Assert.Equal((45d, 0d, false), (reading.Left!.Value, reading.Over!.Value, reading.Spent));

        // An SSH device on the account during the first rise makes that the user's too.
        history.AddRange([Seen(Kst(11, 10, 9, 30), 7, "ssh:box"), Seen(Kst(11, 10, 11, 30), 7, "ssh:box"), Seen(Kst(11, 10, 11, 35), null, "ssh:box")]);
        var both = View(Canonical(), history);
        AssertClose([0, 35, 0, 0, 0, 0, 0], both.Days.Select(day => day.Used!.Value), 9);
        Assert.Equal(25, both.Resets.Single(item => item.ProfileId == 7).Left!.Value, 9);
        // The SSH device's hold is not this PC's.
        Assert.Equal(new[] { Kst(11, 10, 12) }, both.Holds.Select(hold => hold.StartsAt).ToArray());
    }

    [Fact]
    public void APastResetSaysWhatWasLeftWhatWentOverAndWhetherTheAccountRanDry()
    {
        var shared = Kst(11, 13, 15);
        var view = View(Canonical(),
        [
            Poll(6, Kst(11, 12, 10, 55), 60, Kst(11, 12, 11)),
            // 이더블에스: 10 by other people, then 40 on this PC, on top of the 40 it had when the history began.
            Poll(7, Kst(11, 11, 10), 40, shared), Poll(7, Kst(11, 11, 10, 5), 50, shared),
            Poll(7, Kst(11, 11, 12), 50, shared), Poll(7, Kst(11, 11, 13), 90, shared),
            Seen(Kst(11, 11, 12), 7), Seen(Kst(11, 11, 13), 7), Seen(Kst(11, 11, 13, 5), null),
            Poll(7, Kst(11, 13, 14, 55), 90, shared),
            Poll(1, Kst(11, 15, 15, 55), 99.7, Kst(11, 15, 16)),
        ]);

        Assert.Equal(new[] { 6, 7, 1, 8 }, view.Resets.Select(reset => reset.ProfileId).ToArray());
        Assert.Equal(new[] { Kst(11, 12, 11), shared, Kst(11, 15, 16), Kst(11, 16, 8) }, view.Resets.Select(reset => reset.ResetsAt).ToArray());
        AssertClose([75d / 168, 103d / 168, 152d / 168, 1], view.Resets.Select(reset => reset.At), 9);
        Assert.Equal(new[] { false, false, false, true }, view.Resets.Select(reset => reset.Upcoming).ToArray());
        Assert.All(view.Resets, reset => Assert.False(reset.Projected));

        var (whole, part, dry, ahead) = (view.Resets[0], view.Resets[1], view.Resets[2], view.Resets[3]);
        Assert.Equal((40d, 0d, false), (whole.Left!.Value, whole.Over!.Value, whole.Spent));
        // 80 of the 90 were the user's, 10 beyond the share of 70; the 10 still left were not the user's to use.
        Assert.Equal((0d, 10d, false), (Math.Round(part.Left!.Value, 9), Math.Round(part.Over!.Value, 9), part.Spent));
        Assert.Equal((0.3, 0d, true), (Math.Round(dry.Left!.Value, 9), dry.Over!.Value, dry.Spent));
        Assert.True(ahead.Left is null && ahead.Over is null && !ahead.Spent);
        Assert.Equal(40.3, view.Summary.Lost, 9);
    }

    [Fact]
    public void AResetWithNoReadingOfItsWindowClaimsNothing()
    {
        var view = View(Canonical(),
        [
            // An idle window names no reset, and a reading from the window before names another one.
            Poll(6, Kst(11, 12, 10, 55), 0, null),
            Poll(7, Kst(11, 5, 9), 60, Kst(11, 6, 15)),
            // A reading that names the reset but is more than a week older than it is not of that window.
            Poll(1, Kst(11, 8, 15), 50, Kst(11, 15, 16)),
        ]);

        Assert.All(view.Resets, reset => Assert.True(reset.Left is null && reset.Over is null && !reset.Spent));
        Assert.Equal(0, view.Summary.Lost);
    }

    [Fact]
    public void RowsTellTheUsersPartOfASharedAccountAndAFullAccountAfterALapsedWindow()
    {
        var accounts = Canonical(account => account.ProfileId switch
        {
            1 => account with { UsedPercent = 80, ResetsAt = Kst(11, 15, 16) },
            6 => account with { UsedPercent = null },
            7 => account with { UsedPercent = 50 },
            _ => account,
        });
        var view = View(accounts,
        [
            // Before 이더블에스's window began: not counted against it.
            Poll(7, Kst(11, 13, 9), 60, Kst(11, 13, 15)), Poll(7, Kst(11, 13, 9, 5), 70, Kst(11, 13, 15)),
            // The new window read 10 when no device of the user's had been on it, and rose 15 more the same way.
            Poll(7, Kst(11, 14, 10), 10, Kst(11, 20, 15)), Poll(7, Kst(11, 14, 10, 5), 25, Kst(11, 20, 15)),
        ]);
        WeekTimelineRow Row(int id) => view.Accounts.Single(row => row.ProfileId == id);

        // Other people used 25 of the 50: the user has 45 of the share of 70 left.
        Assert.Equal((50d, 25d, 45d), (Row(7).Remaining!.Value, Row(7).MineUsed!.Value, Row(7).MineLeft!.Value));
        Assert.Equal((95d, 5d, 95d), (Row(8).Remaining!.Value, Row(8).MineUsed!.Value, Row(8).MineLeft!.Value));
        Assert.True(Row(6).Remaining is null && Row(6).MineUsed is null && Row(6).MineLeft is null);
        Assert.True(Row(6).Planned);

        // 개인 1's reset passed at 16:00 and the 80 used is of a window that is over.
        Assert.Equal((100d, 0d, 100d), (Row(1).Remaining!.Value, Row(1).MineUsed!.Value, Row(1).MineLeft!.Value));
        Assert.True(Row(1).ResetProjected);
        Assert.Equal(Kst(11, 22, 16), Row(1).ResetsAt);
        Assert.Equal(new[] { 6, 7, 1, 8 }, view.Accounts.Select(row => row.ProfileId).ToArray());
        Assert.True(Row(8).IsCurrent && Row(8).SmartCandidate && !Row(7).IsCurrent);
        // An account whose use is not known cannot take a turn; the projected one goes last.
        Assert.Equal((8, 7), (view.Summary.Turn!.ProfileId, view.Summary.Next!.ProfileId));
    }

    [Fact]
    public void AnEarlierWeekShowsItsOwnHoldsDaysAndResetsAndNoTurn()
    {
        var reset = Kst(11, 5, 11);
        QuotaHistoryEntry[] history =
        [
            Poll(6, Kst(11, 1, 10), 5, reset),
            Poll(6, Kst(11, 3, 10), 10, reset), Seen(Kst(11, 3, 10), 6),
            Poll(6, Kst(11, 3, 20), 30, reset), Seen(Kst(11, 3, 20), 6),
            Seen(Kst(11, 3, 20, 5), null),
        ];
        var view = View(Canonical(), history, week: -1);

        Assert.Equal("ok", view.State);
        Assert.Equal(-1, view.Week);
        Assert.Equal(Kst(11, 2, 8), view.StartsAt);
        Assert.Equal(Kst(11, 9, 8), view.EndsAt);
        Assert.Null(view.NowAt);
        Assert.True(view.HasEarlier);
        Assert.Equal(Kst(11, 1, 10), view.RecordStartsAt);
        Assert.Equal(new[] { 6, 7, 1, 8 }, view.Segments.Select(segment => segment.ProfileId).ToArray());
        Assert.Equal(Kst(11, 2, 8), view.Segments[0].StartsAt);
        AssertClose([29.5946, 25.8108, 29.4054, 0], view.Segments.Select(segment => segment.SlackHours), 4);

        var hold = Assert.Single(view.Holds);
        Assert.Equal((6, Kst(11, 3, 10), Kst(11, 3, 20)), (hold.ProfileId, hold.StartsAt, hold.EndsAt));
        Assert.Equal(26d / 168, hold.At, 9);
        Assert.Equal((90d, 70d, false), (hold.RemainingFrom!.Value, hold.RemainingTo!.Value, hold.IsOpen));

        // The 5 that rose over the two days before the hold are spread: 24 of those 48 hours are the first day's.
        AssertClose([2.5, 5 * 2d / 48 + 20, 0, 0, 0, 0, 0], view.Days.Select(day => day.Used!.Value), 9);
        Assert.All(view.Days, day => Assert.False(day.IsToday));
        Assert.Equal(22.5 + 5 * 2d / 48, view.Summary.Used, 9);
        Assert.Equal(370, view.Summary.PlannedSoFar, 9);
        Assert.Null(view.Summary.Turn);
        Assert.Null(view.Summary.Next);

        Assert.Equal(new[] { Kst(11, 5, 11), Kst(11, 6, 15), Kst(11, 8, 16), Kst(11, 9, 8) }, view.Resets.Select(item => item.ResetsAt).ToArray());
        Assert.All(view.Resets, item => Assert.False(item.Upcoming || item.Projected));
        Assert.Equal(70, view.Resets[0].Left!.Value, 9);
        Assert.Equal(70, view.Summary.Lost, 9);

        // The rows still tell the accounts as they are now.
        Assert.Equal(95, view.Accounts.Single(row => row.ProfileId == 8).MineLeft!.Value, 6);
        Assert.Equal(Kst(11, 19, 11), view.Accounts.Single(row => row.ProfileId == 6).ResetsAt);

        var current = View(Canonical(), history);
        Assert.True(current.HasEarlier);
        Assert.Empty(current.Holds);
        Assert.Equal(0, current.Summary.Used, 9);
        // Nothing was recorded before the week before last began.
        Assert.False(View(Canonical(), history, week: -2).HasEarlier);
    }

    [Fact]
    public void TheWeekAskedForStaysWithinFourWeeksBack()
    {
        QuotaHistoryEntry[] history = [Poll(6, Kst(10, 1, 10), 5, Kst(10, 1, 11))];
        var oldest = View(Canonical(), history, week: -9);

        Assert.Equal(WeekTimelineCalculator.MinimumWeek, oldest.Week);
        Assert.Equal(Kst(10, 12, 8), oldest.StartsAt);
        Assert.Equal(Kst(10, 19, 8), oldest.EndsAt);
        Assert.False(oldest.HasEarlier);
        Assert.True(View(Canonical(), history, week: -3).HasEarlier);
        Assert.Equal(0, View(Canonical(), history, week: 3).Week);
    }

    [Fact]
    public void AnEmptyRequestGivesNumbersThatCanAllBeWritten()
    {
        var view = WeekTimelineCalculator.Calculate(new WeekTimelineRequest(Now, 0, [], []));

        Assert.Equal("empty", view.State);
        Assert.False(view.HasEarlier);
        Assert.Null(view.RecordStartsAt);
        Assert.Null(view.NowAt);
        Assert.Equal((0d, 0d), (view.TotalShare, view.DailyShare));
        Assert.Equal((0d, 0d, 0d), (view.Summary.Used, view.Summary.PlannedSoFar, view.Summary.Lost));
        Assert.Empty(view.Accounts);
        AssertSerializable(view);

        // Neither do accounts with nothing known about them, or an account whose share is nothing.
        AssertSerializable(View([Account(1, "가", null, used: null), Account(2, "나", null, share: 0, used: null)], []));
        AssertSerializable(View([Account(1, "가", Kst(11, 18, 12), share: 1, used: null)], [Poll(1, Now, 0, null), Poll(1, Now, 0, null)]));
    }
}
