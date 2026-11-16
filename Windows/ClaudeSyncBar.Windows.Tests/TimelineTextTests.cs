using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeSyncBar.App.Model;
using ClaudeSyncBar.Ubuntu.Backend;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Windows.Tests;

/// <summary>The weekly timeline's words on the page and in the quick view, and the view the app keeps for both.</summary>
public sealed class TimelineTextTests
{
    // Sunday 17:44 on this machine's clock, so "today", "tomorrow" and weekdays read the same in any time zone.
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 11, 15, 17, 44, 0, DateTimeKind.Local));

    private static DateTimeOffset Local(int day, int hour, int minute = 0) => new(new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Local));

    private static string Json(DateTimeOffset? time) => time is { } at ? $"\"{at:O}\"" : "null";

    private static JsonNode View(string turn, string next = "null", string resets = "[]", string accounts = "[]", double used = 287, double planned = 338.58) =>
        JsonNode.Parse($$"""
            { "state": "ok", "week": 0, "dailyShare": 52.857, "totalShare": 370, "resets": {{resets}}, "accounts": {{accounts}},
              "summary": { "used": {{used}}, "plannedSoFar": {{planned}}, "lost": 4, "turn": {{turn}}, "next": {{next}} } }
            """)!;

    private static string Turn(string name, bool current, DateTimeOffset? resets, double left, double? lost) =>
        $$"""{ "profileId": 8, "name": "{{name}}", "isCurrent": {{(current ? "true" : "false")}}, "resetsAt": {{Json(resets)}}, "mineLeft": {{left}}, "forecastLost": {{lost?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}} }""";

    [Fact]
    public void TheTurnSaysWhenItResetsWhatIsLeftAndWhatThePlanWouldLeaveUnused()
    {
        var view = View(Turn("개인 2", true, Local(16, 8), 95, 63.58));
        Assert.Equal("지금은 개인 2 차례예요", TimelineText.TurnTitle(view));
        Assert.Equal("내일 08:00에 초기화돼요. 95%가 남았고, 계획대로 하루 53%씩 써도 64%쯤은 사라져요.", TimelineText.TurnCaption(view, Now));
        Assert.Equal("계획대로 하루 53%씩 써도 64%쯤 사라져요", TimelineText.StripCaption(view));

        // Less than a percent would be left over: the plan uses it up.
        var used = View(Turn("개인 2", true, Local(15, 23, 30), 5, 0.4));
        Assert.Equal("23:30에 초기화돼요. 5%가 남았고, 계획대로 하루 53%씩 쓰면 그 전에 다 써요.", TimelineText.TurnCaption(used, Now));
        Assert.Equal("계획대로 쓰면 초기화 전에 다 써요", TimelineText.StripCaption(used));

        // A week not used yet has no reset to count down to.
        var unknown = View(Turn("그립랩스", true, null, 100, null));
        Assert.Equal("100%가 남았어요. 초기화 시각은 한 번 써야 정해져요.", TimelineText.TurnCaption(unknown, Now));
        Assert.Equal("초기화 시각은 한 번 써야 정해져요", TimelineText.StripCaption(unknown));
    }

    [Fact]
    public void ATurnThatIsNotTheAccountInUseNamesTheOneInUse()
    {
        string Accounts(string inUse) => $$"""[{ "profileId": 8, "name": "실험 계정", "isCurrent": false }, { "profileId": 2, "name": "{{inUse}}", "isCurrent": true }]""";
        var turn = Turn("실험 계정", false, Local(16, 8), 5, 0);
        Assert.EndsWith("그 전에 다 써요. 지금 사용 중인 계정은 개인 2예요.", TimelineText.TurnCaption(View(turn, accounts: Accounts("개인 2")), Now));
        // A name that ends in a consonant takes "이에요".
        Assert.EndsWith(" 지금 사용 중인 계정은 업무 계정이에요.", TimelineText.TurnCaption(View(turn, accounts: Accounts("업무 계정")), Now));
        Assert.EndsWith(" 지금 사용 중인 계정은 개인 1이에요.", TimelineText.TurnCaption(View(turn, accounts: Accounts("개인 1")), Now));
        // Latin names read with a final ㄴ, ㄹ, ㅁ or ㅂ take it too; the others are read with a vowel after them.
        Assert.Equal(("이에요", "이에요", "이에요", "이에요"), (TimelineText.Copula("Main"), TimelineText.Copula("x@gmail.com"), TimelineText.Copula("Lab"), TimelineText.Copula("TEAM")));
        Assert.Equal(("예요", "예요", "예요"), (TimelineText.Copula("Pro"), TimelineText.Copula("Max"), TimelineText.Copula("Work")));
        // With no account applied there is none to name.
        Assert.EndsWith("그 전에 다 써요.", TimelineText.TurnCaption(View(turn), Now));
    }

    [Fact]
    public void WithNoShareLeftTheFirstResetAheadIsNamed()
    {
        var resets = $$"""
            [{ "name": "개인 1", "resetsAt": {{Json(Local(15, 16))}}, "upcoming": false, "left": 4 },
             { "name": "그립랩스", "resetsAt": {{Json(Local(19, 11))}}, "upcoming": true }]
            """;
        var view = View("null", resets: resets);
        Assert.Equal("지금 쓸 수 있는 내 몫이 없어요", TimelineText.TurnTitle(view));
        Assert.Equal("가장 먼저 초기화되는 계정: 그립랩스 · 11.19 (목) 11:00", TimelineText.TurnCaption(view, Now));
        Assert.Equal("", TimelineText.StripCaption(view));
        Assert.Equal("", TimelineText.TurnCaption(View("null"), Now));
        // A reset known only from the history is said to be expected, as its tick and its row say.
        var projected = $$"""[{ "name": "그립랩스", "resetsAt": {{Json(Local(16, 8))}}, "upcoming": true, "projected": true }]""";
        Assert.Equal("가장 먼저 초기화되는 계정: 그립랩스 · 내일 08:00 예상", TimelineText.TurnCaption(View("null", resets: projected), Now));
    }

    [Fact]
    public void AResetSaysItsDayAheadAndWhatItTookBehind()
    {
        string Status(string fields) => TimelineText.ResetStatus(JsonNode.Parse("{" + fields + "}"), Now);
        Assert.Equal("오늘", Status($"\"upcoming\": true, \"resetsAt\": {Json(Local(15, 23))}"));
        Assert.Equal("내일", Status($"\"upcoming\": true, \"resetsAt\": {Json(Local(16, 8))}"));
        Assert.Equal("목", Status($"\"upcoming\": true, \"resetsAt\": {Json(Local(19, 11))}"));
        Assert.Equal("금 예상", Status($"\"upcoming\": true, \"projected\": true, \"resetsAt\": {Json(Local(20, 15))}"));
        // Passed: run dry first, then over the share, then what was left of it, then nothing left, then nothing known.
        Assert.Equal("다 씀", Status("\"spent\": true, \"over\": 12, \"left\": 0"));
        Assert.Equal("내 몫 17% 넘김", Status("\"spent\": false, \"over\": 17.2, \"left\": 0"));
        Assert.Equal("4% 사라짐", Status("\"spent\": false, \"over\": 0, \"left\": 4.4"));
        Assert.Equal("남김 없음", Status("\"spent\": false, \"over\": 0.5, \"left\": 0.6"));
        Assert.Equal("", Status("\"spent\": false, \"over\": null, \"left\": null"));
    }

    [Fact]
    public void TheLostAmountNamesTheResetThatLeftTheMost()
    {
        string Reset(string name, int day, string left, bool upcoming = false) =>
            $$"""{ "name": "{{name}}", "resetsAt": {{Json(Local(day, 16))}}, "upcoming": {{(upcoming ? "true" : "false")}}, "left": {{left}} }""";
        Assert.Equal("초기화 때 남긴 몫이 없어요", TimelineText.LostCaption(View("null", resets: $"[{Reset("개인 1", 12, "0.4")}, {Reset("그립랩스", 13, "null")}]")));
        Assert.Equal("개인 1 · 11.15 (일) 16:00 초기화 때", TimelineText.LostCaption(View("null", resets: $"[{Reset("개인 1", 15, "4")}, {Reset("개인 2", 16, "null", true)}]")));
        Assert.Equal("연구 계정 · 11.13 (금) 16:00 초기화 때 외 2곳",
            TimelineText.LostCaption(View("null", resets: $"[{Reset("개인 1", 10, "38")}, {Reset("연구 계정", 13, "40")}, {Reset("실험", 14, "1")}, {Reset("업무", 14, "0")}]")));
        // No reset passed yet, or none whose leftover was read: neither is a week that left nothing.
        Assert.Equal("아직 지난 초기화가 없어요", TimelineText.LostCaption(View("null", resets: $"[{Reset("개인 2", 16, "null", true)}]")));
        Assert.Equal("초기화 때 남은 양을 읽지 못했어요", TimelineText.LostCaption(View("null", resets: $"[{Reset("개인 1", 12, "null")}]")));
    }

    [Fact]
    public void TheLostFigureCountsWhatTheResetLabelsCount()
    {
        string Reset(string left, bool upcoming = false) => $$"""{ "name": "개인 1", "resetsAt": {{Json(Local(12, 16))}}, "upcoming": {{(upcoming ? "true" : "false")}}, "left": {{left}} }""";
        // Less than a percent is "남김 없음" on its tick and in the caption, so it is not counted here either.
        Assert.Equal("0%", TimelineText.Lost(View("null", resets: $"[{Reset("0.7")}, {Reset("0.6")}]")));
        Assert.Equal("38%", TimelineText.Lost(View("null", resets: $"[{Reset("38.4")}, {Reset("0.9")}, {Reset("0.6")}]")));
        Assert.Equal("0%", TimelineText.Lost(View("null", resets: $"[{Reset("null", true)}]")));
        Assert.Equal("—", TimelineText.Lost(View("null", resets: $"[{Reset("null")}, {Reset("null")}]")));
    }

    [Fact]
    public void WhatIsLeftIsSaidAsTheUsagePageSaysIt()
    {
        // A sliver is not rounded up to a whole percent, nor nearly all to all of it.
        Assert.Equal(("0.6%", "99%", "0%", "100%", "53%", "—"),
            (TimelineText.Left(0.6), TimelineText.Left(99.6), TimelineText.Left(0), TimelineText.Left(100), TimelineText.Left(52.857), TimelineText.Left(null)));
        Assert.Equal("72 → 0.6%", TimelineText.LeftRange(72.2, 0.6));
        Assert.Equal("100 → 24%", TimelineText.LeftRange(100, 24));
        var turn = View(Turn("개인 2", true, Local(16, 8), 99.6, 63.58));
        Assert.Contains(" 99%가 남았고", TimelineText.TurnCaption(turn, Now));
    }

    [Fact]
    public void AnEmptyPlanSaysWhatWouldBringOne()
    {
        string Rows(params string[] reasons) => "[" + string.Join(", ", reasons.Select(reason => $$"""{ "planned": false, "excluded": "{{reason}}" }""")) + "]";
        Assert.Equal("계정을 추가하거나 다시 로그인하면 여기에 일주일 계획이 보여요.", TimelineText.EmptyCaption(View("null", accounts: "[]")));
        Assert.Equal("계정을 추가하거나 다시 로그인하면 여기에 일주일 계획이 보여요.", TimelineText.EmptyCaption(View("null", accounts: Rows("login", "reset"))));
        Assert.Equal("내 몫을 1% 이상으로 정하면 여기에 일주일 계획이 보여요.", TimelineText.EmptyCaption(View("null", accounts: Rows("share", "share"))));
        Assert.Equal("내 몫을 1% 이상으로 정하거나 다시 로그인하면 여기에 일주일 계획이 보여요.", TimelineText.EmptyCaption(View("null", accounts: Rows("share", "login"))));
    }

    [Fact]
    public void AValueNotFreshIsSaidToBeAnEarlierOne()
    {
        var at = DateTimeOffset.UtcNow;
        JsonNode Account(TimeSpan age, string error = "null", bool login = false) => JsonNode.Parse($$"""
            { "id": 4, "needsLogin": {{(login ? "true" : "false")}},
              "usage": { "weekly": { "usedPercent": 19, "resetsAt": {{Json(at.AddDays(3))}} }, "updatedAt": {{Json(at - age)}}, "error": {{error}} } }
            """)!;
        Assert.Null(TimelineText.Stale(Account(TimeSpan.FromMinutes(1)), at, unavailable: false));
        Assert.Equal("갱신 지연 · 이전 수치", TimelineText.Stale(Account(TimeSpan.FromMinutes(30)), at, unavailable: false));
        Assert.Equal("조회 실패 · 이전 수치", TimelineText.Stale(Account(TimeSpan.FromMinutes(1), "\"timeout\""), at, unavailable: false));
        Assert.Equal("재로그인 필요 · 이전 수치", TimelineText.Stale(Account(TimeSpan.FromMinutes(1), login: true), at, unavailable: false));
        Assert.Equal("연결 실패 · 이전 수치", TimelineText.Stale(Account(TimeSpan.FromMinutes(1)), at, unavailable: true));
        // Nothing read, nothing to call earlier.
        Assert.Null(TimelineText.Stale(JsonNode.Parse("""{ "id": 5, "usage": null }"""), at, unavailable: false));
        Assert.Null(TimelineText.Stale(null, at, unavailable: false));
    }

    [Fact]
    public void AViewReadLongAgoNoLongerSaysWhereNowIs()
    {
        var at = DateTimeOffset.UtcNow;
        JsonNode View(DateTimeOffset read, DateTimeOffset ends) => new JsonObject { ["now"] = read.ToString("O"), ["endsAt"] = ends.ToString("O") };
        Assert.True(TimelineText.Current(View(at.AddMinutes(-1), at.AddDays(2)), at));
        Assert.False(TimelineText.Current(View(at.AddMinutes(-6), at.AddDays(2)), at));
        // A week over since it was read has another plan.
        Assert.False(TimelineText.Current(View(at.AddMinutes(-1), at.AddSeconds(-1)), at));
        Assert.False(TimelineText.Current(new JsonObject(), at));
        Assert.False(TimelineText.Current(null, at));
    }

    [Fact]
    public void AmountsAreWholePercentsAndSegmentEdgesFullHours()
    {
        Assert.Equal(("53%", "64%", "0%", "—"), (TimelineText.Percent(52.857), TimelineText.Percent(63.58), TimelineText.Percent(0.4), TimelineText.Percent(null)));
        Assert.Equal("339", TimelineText.Whole(338.58));
        Assert.Equal("0", TimelineText.Whole(-0.2));
        // 05:24 is said as 05:00 and 12:36 as 13:00; the reset an account's week began at is said to the minute.
        Assert.Equal("수 05:00", TimelineText.DayHour(Local(11, 5, 24)));
        Assert.Equal("목 13:00", TimelineText.DayHour(Local(12, 12, 36)));
        Assert.Equal("월 00:00", TimelineText.DayHour(Local(15, 23, 40)));
        Assert.Equal("금 15:00", TimelineText.DayTime(Local(13, 15)));
        // Only the planned accounts are counted: they are the ones the strip draws and the plan covers.
        Assert.Equal("4개 계정 합쳐 287% 씀 · 지금까지 계획 339%", TimelineText.StripCount(View("null",
            accounts: """[{ "planned": true }, { "planned": true }, { "planned": true }, { "planned": true }, { "planned": false, "excluded": "login" }]""")));
    }

    [Fact]
    public void AnEdgeThatIsAResetKeepsItsMinutesAndOneOutsideTheRangeItsDate()
    {
        // A week from Friday 20:30 to Friday 20:30: the header names Saturday the 14th to Friday the 20th.
        var days = new JsonArray();
        for (var i = 0; i < 7; i++) days.Add(JsonNode.Parse($"{{ \"startsAt\": {Json(Local(13 + i, 20, 30))} }}"));
        var view = JsonNode.Parse($$"""
            { "startsAt": {{Json(Local(13, 20, 30))}}, "endsAt": {{Json(Local(20, 20, 30))}},
              "segments": [{ "profileId": 3, "resetsAt": {{Json(Local(16, 19, 10))}} }, { "profileId": 5, "resetsAt": {{Json(Local(18, 20, 50))}} },
                           { "profileId": 4, "resetsAt": {{Json(Local(20, 20, 30))}} }] }
            """)!;
        view["days"] = days;
        Assert.Equal("11.14 (토) ~ 11.20 (금)", TimelineText.Range(view));
        // The week's start is a reset and lies a day before the range: its date says which Friday.
        Assert.Equal("11.13 (금) 20:30", TimelineText.EdgeText(view, 3, Local(13, 20, 30)));
        // Inner edges to the hour.
        Assert.Equal("일 18:00", TimelineText.EdgeText(view, 3, Local(15, 18, 24)));
        Assert.Equal("목 13:00", TimelineText.EdgeText(view, 4, Local(19, 12, 36)));
        // The binding account's segment ends at its reset and the week: to the minute, not at 21:00 past it.
        Assert.Equal("금 20:30", TimelineText.EdgeText(view, 4, Local(20, 20, 30)));
        // Within a minute of its own reset an edge is that reset.
        Assert.Equal("월 19:10", TimelineText.EdgeText(view, 3, Local(16, 19, 9).AddSeconds(30)));
        // Rounding up would pass the account's reset at 20:50: the hour before is said instead.
        Assert.Equal("수 20:00", TimelineText.EdgeText(view, 5, Local(18, 20, 40)));
        Assert.Equal(Local(20, 20, 30), TimelineText.Edge(view, 4, Local(20, 20, 30)));
        // Knowing neither the week nor the account, an edge is an inner one: to the hour, the weekday alone.
        Assert.Equal("금 21:00", TimelineText.EdgeText(new JsonObject(), null, Local(13, 20, 30)));
    }

    [Fact]
    public void ADayIsNamedByTheDateItsMiddleFallsOn()
    {
        // Days that run from 08:00 to 08:00: the first is Monday the 9th, the last Sunday the 15th.
        var days = new JsonArray();
        for (var i = 0; i < 7; i++) days.Add(JsonNode.Parse($"{{ \"startsAt\": {Json(Local(9 + i, 8))} }}"));
        var view = new JsonObject { ["days"] = days };
        Assert.Equal("11.09 (월) ~ 11.15 (일)", TimelineText.Range(view));
        Assert.Equal(("월 9", "월"), (TimelineText.DayLabel(days[0]), TimelineText.DayLetter(days[0])));
        Assert.Equal("일 15", TimelineText.DayLabel(days[6]));
        // A day that begins in the evening belongs to the date most of it falls on.
        Assert.Equal("화 10", TimelineText.DayLabel(JsonNode.Parse($"{{ \"startsAt\": {Json(Local(9, 16, 15))} }}")));
        Assert.Equal("", TimelineText.Range(new JsonObject()));
        // Today is the day named by today's date, also late in the evening when now already lies in the next day's column.
        var evening = JsonNode.Parse($"{{ \"startsAt\": {Json(Local(14, 20, 30))} }}");
        var next = JsonNode.Parse($"{{ \"startsAt\": {Json(Local(15, 20, 30))} }}");
        Assert.Equal(("일 15", "월 16"), (TimelineText.DayLabel(evening), TimelineText.DayLabel(next)));
        Assert.True(TimelineText.IsToday(evening, Local(15, 22)));
        Assert.False(TimelineText.IsToday(next, Local(15, 22)));
    }

    [Fact]
    public async Task TheAppKeepsThisWeeksViewAndAFailureLeavesItInPlace()
    {
        var now = DateTimeOffset.UtcNow;
        DashboardUsage Usage(double used, TimeSpan resetsIn) => new(new(used, now + resetsIn, 604800), now);
        var service = new Failing(new DemoService(
        [
            new(1, "main@example.com", "개인 계정", false, false, Usage(32, new(1, 19, 12, 0)), "Max 20x"),
            new(2, "work@example.com", "업무 계정", false, false, Usage(76, new(4, 2, 24, 0)), "Max 20x"),
        ], activeProfileId: 2));
        var state = new AppState(new ServiceClient(service));
        var changes = 0;
        state.Changed += () => Interlocked.Increment(ref changes);
        Assert.Null(state.Timeline);

        await state.RefreshTimelineAsync();
        var first = Assert.IsType<JsonObject>(state.Timeline);
        Assert.Equal(("ok", 0), (AppState.Str(first["state"]), AppState.Int(first["week"])));
        Assert.Null(state.TimelineError);
        Assert.Equal(1, changes);
        // What the strip and the page read from it.
        Assert.Equal(2, TimelineText.Items(first["segments"]).Count);
        Assert.Equal(7, TimelineText.Items(first["days"]).Count);
        Assert.StartsWith("지금은 ", TimelineText.TurnTitle(first));
        Assert.StartsWith("2개 계정 합쳐 ", TimelineText.StripCount(first));
        Assert.NotEqual("", TimelineText.Range(first));

        // The service does not answer: the view stays, the reason is kept, and the screens are told once.
        service.Fail = true;
        await state.RefreshTimelineAsync();
        Assert.Same(first, state.Timeline);
        Assert.Equal("서비스에 연결하지 못했어요.", state.TimelineError);
        Assert.Equal(2, changes);
        await state.RefreshTimelineAsync();
        Assert.Equal(2, changes);

        service.Fail = false;
        await state.RefreshTimelineAsync();
        Assert.Null(state.TimelineError);
        Assert.NotSame(first, state.Timeline);
        Assert.Equal(3, changes);
    }

    private sealed class Failing(DemoService inner) : IManagementService
    {
        public volatile bool Fail;
        public bool ShutdownRequested => inner.ShutdownRequested;

        public Task<object?> DispatchAsync(string method, JsonElement parameters, CancellationToken token) =>
            Fail ? throw new RpcException("unreachable", "서비스에 연결하지 못했어요.") : inner.DispatchAsync(method, parameters, token);

        public Task StopAsync() => inner.StopAsync();
        public void Dispose() => inner.Dispose();
    }
}
