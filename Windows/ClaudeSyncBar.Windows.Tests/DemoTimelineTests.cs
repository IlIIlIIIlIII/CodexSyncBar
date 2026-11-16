using System.Text.Json;
using ClaudeSyncBar.Ubuntu.Backend;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Windows.Tests;

/// <summary>The demo's made-up week goes through the real calculation and gives the timeline something to show in every row.</summary>
public sealed class DemoTimelineTests
{
    private static readonly (double Used, TimeSpan ResetsIn)[] Weeks =
        [(32, new(1, 19, 12, 0)), (76, new(4, 2, 24, 0)), (95, new(0, 16, 48, 0)), (19, new(4, 18, 12, 0))];

    // The accounts the app's demo shows: the second is in use, the fourth starts shared and the fifth needs a login.
    private static AccountView[] Accounts(DateTimeOffset now)
    {
        DashboardUsage Usage(int index) => new(new(Weeks[index].Used, now + Weeks[index].ResetsIn, 604800), now);
        return
        [
            new(1, "main@example.com", "개인 계정", false, false, Usage(0), "Max 20x"),
            new(2, "work@example.com", "업무 계정", false, false, Usage(1), "Max 20x"),
            new(3, "lab@example.com", "실험 계정", false, false, Usage(2), "Max 5x"),
            new(4, "research@example.com", "연구 계정", false, false, Usage(3), "Pro"),
            new(5, "sub@example.com", "보조 계정", false, true, null, "Pro"),
        ];
    }

    private static async Task<object?> CallAsync(DemoService service, string method, string parameters = "{}")
    {
        using var document = JsonDocument.Parse(parameters);
        return await service.DispatchAsync(method, document.RootElement, CancellationToken.None);
    }

    private static async Task<WeekTimelineView> ViewAsync(DemoService service, int? week = null) =>
        Assert.IsType<WeekTimelineView>(await CallAsync(service, "timeline", week is null ? "{}" : $"{{\"week\":{week}}}"));

    private static Task ShareAsync(DemoService service, int profileId, string percent) =>
        CallAsync(service, "account.share", $"{{\"profileId\":{profileId},\"percent\":{percent}}}");

    private static WeekTimelineView View(DemoWeek week, AccountView[] accounts, DateTimeOffset now, int index) =>
        WeekTimelineCalculator.Calculate(new(now, index, accounts.Select(a => new WeekTimelineAccount(a.Id, a.Alias, a.Id == 4 ? 70 : 100,
            !a.NeedsLogin, a.Usage?.Weekly?.UsedPercent, a.Usage?.Weekly?.ResetsAt, a.Id == 2, true)).ToArray(), week.History));

    [Fact]
    public async Task TheDemoTimelineAgreesWithTheUsageTheSnapshotShows()
    {
        var accounts = Accounts(DateTimeOffset.UtcNow);
        var view = await ViewAsync(new DemoService(accounts, activeProfileId: 2));

        Assert.Equal(("ok", 0), (view.State, view.Week));
        Assert.NotNull(view.NowAt);
        // The fourth account starts at a share of 70; the week runs in reset order up to the account with no room left.
        Assert.Equal(370, view.TotalShare);
        Assert.Equal(new[] { 3, 1, 2, 4 }, view.Segments.Select(segment => segment.ProfileId));
        Assert.Equal(new[] { 100, 100, 100, 70 }, view.Segments.Select(segment => segment.Share));
        Assert.Equal(0, view.Segments[^1].SlackHours);

        foreach (var account in accounts.Take(4))
        {
            var weekly = account.Usage!.Weekly!;
            var row = view.Accounts.Single(row => row.ProfileId == account.Id);
            Assert.True(row.Planned && !row.ResetProjected);
            Assert.Equal(account.Alias, row.Name);
            Assert.Equal(100 - weekly.UsedPercent, row.Remaining);
            // The reset is the one read, to the nearest five minutes.
            Assert.InRange((row.ResetsAt!.Value - weekly.ResetsAt!.Value).Duration(), TimeSpan.Zero, TimeSpan.FromMinutes(2.5));
            Assert.Equal(account.Id == 2, row.IsCurrent);
            // An account that is all the user's has all its use counted as the user's.
            if (account.Id != 4) Assert.Equal((weekly.UsedPercent, 100 - weekly.UsedPercent), (row.MineUsed!.Value, row.MineLeft!.Value));
        }

        // Of the shared account's 19% some was other people's, and what is left to the user is within the share.
        var shared = view.Accounts.Single(row => row.ProfileId == 4);
        Assert.Equal(70, shared.Share);
        Assert.InRange(shared.MineUsed!.Value, 1, 19);
        Assert.Equal(70 - shared.MineUsed.Value, shared.MineLeft!.Value);
        var idle = view.Accounts[^1];
        Assert.Equal((5, "login", false), (idle.ProfileId, idle.Excluded, idle.Planned));
        Assert.Null(idle.Remaining);

        // This PC went through several accounts and is on the one in use now, where the usage page has it.
        Assert.True(view.Holds.Select(hold => hold.ProfileId).Distinct().Count() >= 3);
        Assert.Equal((2, true, 24d), (view.Holds[^1].ProfileId, view.Holds[^1].IsOpen, view.Holds[^1].RemainingTo!.Value));
        Assert.DoesNotContain(view.Holds, hold => hold.ProfileId == 5);
        // The account nearest its reset has the turn, which is not the one in use.
        Assert.Equal((3, false, 5d), (view.Summary.Turn!.ProfileId, view.Summary.Turn.IsCurrent, view.Summary.Turn.MineLeft!.Value));
        Assert.Equal(1, view.Summary.Next!.ProfileId);
        Assert.True(view.Summary.Used > 0);
        Assert.True(view.HasEarlier);
        Assert.NotEmpty(JsonSerializer.Serialize(view, Wire.Json));
    }

    [Fact]
    public async Task AShareSetInTheDemoMovesTheSegmentsAndAnEarlierWeekAnswers()
    {
        var service = new DemoService(Accounts(DateTimeOffset.UtcNow), activeProfileId: 2);
        var start = await ViewAsync(service);

        // The week before ends where this one begins, and it is whole: every day has its use.
        var earlier = await ViewAsync(service, -1);
        Assert.Equal(("ok", -1), (earlier.State, earlier.Week));
        Assert.Equal(start.StartsAt, earlier.EndsAt);
        Assert.Null(earlier.NowAt);
        Assert.Null(earlier.Summary.Turn);
        Assert.All(earlier.Days, day => Assert.NotNull(day.Used));
        Assert.Equal(4, earlier.Resets.Count(reset => !reset.Upcoming));
        Assert.Equal(WeekTimelineCalculator.MinimumWeek, (await ViewAsync(service, -9)).Week);

        await ShareAsync(service, 2, "50");
        var halved = await ViewAsync(service);
        Assert.Equal(320, halved.TotalShare);
        var segment = halved.Segments.Single(segment => segment.ProfileId == 2);
        Assert.Equal((50, 50d / 320), (segment.Share, Math.Round(segment.Width, 9)));
        // The others each get more of the week, and the half share is already used up.
        Assert.True(halved.Segments.Single(segment => segment.ProfileId == 3).Width > start.Segments.Single(segment => segment.ProfileId == 3).Width);
        Assert.Equal(0, halved.Accounts.Single(row => row.ProfileId == 2).MineLeft!.Value);

        // A share of nothing takes the account out of the plan; the whole of it brings it back unshared.
        await ShareAsync(service, 4, "0");
        var without = await ViewAsync(service);
        Assert.Equal(new[] { 1, 2, 3 }, without.Segments.Select(segment => segment.ProfileId).Order());
        Assert.Equal("share", without.Accounts.Single(row => row.ProfileId == 4).Excluded);
        await ShareAsync(service, 4, "100");
        Assert.Equal(350, (await ViewAsync(service)).TotalShare);

        foreach (var bad in new[] { "101", "-1", "12.5", "\"50\"", "null" })
            Assert.Equal("invalid_request", (await Assert.ThrowsAsync<RpcException>(() => ShareAsync(service, 2, bad))).Code);
        Assert.Equal("account_unavailable", (await Assert.ThrowsAsync<RpcException>(() => ShareAsync(service, 9, "50"))).Code);
        Assert.Equal(350, (await ViewAsync(service)).TotalShare);
    }

    [Fact]
    public void AtAnyHourTheDemoWeekHasHoldsLostAmountsAnAccountRunDryAndASharedAccountInUse()
    {
        var first = new DateTimeOffset(2026, 11, 15, 0, 7, 33, TimeSpan.Zero);
        for (var step = 0; step < 14; step++)
        {
            var now = first.AddHours(step * 3.7);
            var accounts = Accounts(now);
            var week = new DemoWeek(now, accounts, 2, new HashSet<int> { 4 });
            var end = now.AddTicks(-(now.UtcTicks % TimeSpan.TicksPerMinute));

            // Nine days of looks in time order, the newest reading what the snapshot shows; nothing reads the account that needs a login.
            Assert.Equal(end.AddDays(-9), week.History[0].At);
            Assert.Equal(end, week.History[^1].At);
            Assert.All(week.History.Zip(week.History.Skip(1)), pair => Assert.True(pair.First.At < pair.Second.At));
            var newest = week.History[^1].Samples!;
            Assert.Equal(accounts.Take(4).Select(a => (a.Id, (double?)a.Usage!.Weekly!.UsedPercent, a.Usage.Weekly.ResetsAt)),
                newest.Select(sample => (sample.ProfileId, sample.WeeklyUsed, sample.WeeklyResetsAt)));
            Assert.Contains(week.History, entry => entry.Kind == QuotaHistoryEntry.Switch && entry.DeviceIds!.Contains("local"));

            var current = View(week, accounts, now, 0);
            Assert.Equal(new[] { 3, 1, 4, 2 }, current.Holds.Select(hold => hold.ProfileId));
            Assert.True(current.Holds[^1].IsOpen);
            var begun = current.Days.Where(day => day.Used is not null).Select(day => day.Used!.Value).ToList();
            Assert.Equal(3, begun.Count);
            Assert.True(begun.Max() - begun.Min() > 10 && begun[0] > 20 && begun[1] > 20);
            var shared = current.Accounts.Single(row => row.ProfileId == 4);
            Assert.InRange(shared.MineUsed!.Value, 5, 18);

            // The week before: all seven days, holds of every account, and what each reset took.
            var earlier = View(week, accounts, now, -1);
            Assert.Equal(end.AddDays(-9), earlier.RecordStartsAt);
            Assert.False(earlier.HasEarlier);
            Assert.All(earlier.Days, day => Assert.True(day.Used > 5));
            Assert.True(earlier.Days.Max(day => day.Used) - earlier.Days.Min(day => day.Used) > 15);
            Assert.Equal(new[] { 3, 1, 2, 4, 3, 2, 3 }, earlier.Holds.Select(hold => hold.ProfileId));
            Assert.Equal(new[] { 3, 1, 2, 4 }, earlier.Resets.Select(reset => reset.ProfileId));
            Assert.All(earlier.Resets, reset => Assert.False(reset.Upcoming));
            // Two ran dry while this PC held them, well before their resets; one left 38% and the shared one part of the user's 70%.
            Assert.Equal((true, 0d), (earlier.Resets[0].Spent, earlier.Resets[0].Left!.Value));
            Assert.Equal((false, 38d), (earlier.Resets[1].Spent, earlier.Resets[1].Left!.Value));
            Assert.Equal((true, 0d), (earlier.Resets[2].Spent, earlier.Resets[2].Left!.Value));
            Assert.Equal(0, earlier.Holds[0].RemainingTo!.Value);
            Assert.True(earlier.Resets[0].ResetsAt - earlier.Holds[0].EndsAt > TimeSpan.FromHours(20));
            Assert.InRange(earlier.Resets[3].Left!.Value, 20, 42);
            Assert.Equal(0, earlier.Resets[3].Over!.Value);
            Assert.Equal(38 + earlier.Resets[3].Left!.Value, earlier.Summary.Lost);
        }
    }

    [Fact]
    public async Task ASwitchInTheDemoShowsAsTheNewestHoldAndOtherAccountsStillGetAWeek()
    {
        var service = new DemoService(Accounts(DateTimeOffset.UtcNow), activeProfileId: 2);
        Assert.Equal(2, (await ViewAsync(service)).Holds[^1].ProfileId);
        var preview = Assert.IsType<SwitchPreview>(await CallAsync(service, "preview", "{\"profileId\":1}"));
        var operation = Assert.IsType<OperationView>(await CallAsync(service, "apply", $"{{\"previewId\":\"{preview.PreviewId}\",\"requestId\":\"timeline\"}}"));
        for (var wait = 0; wait < 200 && !operation.IsComplete; wait++)
        {
            await Task.Delay(25);
            operation = Assert.IsType<OperationView>(await CallAsync(service, "operation", $"{{\"operationId\":\"{operation.Id}\"}}"));
        }

        Assert.Equal("completed", operation.State);
        var view = await ViewAsync(service);
        Assert.Equal((1, true), (view.Holds[^1].ProfileId, view.Holds[^1].IsOpen));
        Assert.True(view.Accounts.Single(row => row.ProfileId == 1).IsCurrent);

        // The two accounts of the other demo were read long ago: their weeks are projected, and the answer still stands.
        var fixedDemo = await ViewAsync(new DemoService());
        Assert.Equal(("ok", 200d), (fixedDemo.State, fixedDemo.TotalShare));
        Assert.All(fixedDemo.Accounts, row => Assert.True(row.Planned && row.ResetProjected && row.Share == 100));
        Assert.NotEmpty(JsonSerializer.Serialize(fixedDemo, Wire.Json));
    }
}
