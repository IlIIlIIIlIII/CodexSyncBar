using System.Text.Json.Nodes;
using ClaudeSyncBar.App.Model;

namespace ClaudeSyncBar.Windows.Tests;

public sealed class TaskbarUsageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T12:00:00Z");

    private static JsonObject Account(int id, string name, double? fiveHour, double? weekly, TimeSpan? age = null, bool needsLogin = false,
        TimeSpan? fiveHourReset = null, TimeSpan? weeklyReset = null)
    {
        var account = new JsonObject { ["id"] = id, ["alias"] = name, ["email"] = $"a{id}@example.com", ["needsLogin"] = needsLogin };
        if (fiveHour is null && weekly is null) return account;
        var usage = new JsonObject { ["updatedAt"] = (Now - (age ?? TimeSpan.FromMinutes(1))).ToString("O") };
        if (fiveHour is { } five)
            usage["fiveHour"] = new JsonObject { ["remainingPercent"] = five, ["resetsAt"] = (Now + (fiveHourReset ?? TimeSpan.FromHours(2))).ToString("O") };
        if (weekly is { } week)
            usage["weekly"] = new JsonObject { ["remainingPercent"] = week, ["resetsAt"] = (Now + (weeklyReset ?? TimeSpan.FromDays(3))).ToString("O") };
        account["usage"] = usage;
        return account;
    }

    // The test account resets first among those counted; the one that needs signing in, sooner still, is not counted.
    private static readonly TimeSpan SoonestReset = new(2, 5, 0, 0);

    private static readonly JsonObject[] Accounts =
    [
        Account(1, "개인", 72, 68),
        Account(2, "업무", 9, 24, weeklyReset: TimeSpan.FromDays(4)),
        Account(3, "실험", 100, 5, weeklyReset: SoonestReset),
        Account(4, "연구", 54, 81, age: TimeSpan.FromMinutes(40), weeklyReset: TimeSpan.FromDays(6)),
        Account(5, "보조", null, null, needsLogin: true),
        Account(6, "새 계정", null, null),
    ];

    private static JsonObject Settings(bool fiveHour = true, bool weekly = true, bool taskbar = true) =>
        new() { ["claudeFiveHour"] = fiveHour, ["claudeWeekly"] = weekly, ["taskbarUsage"] = taskbar };

    [Fact]
    public void WeeklySumCountsEveryAccountTheSameAndLeavesOutThoseWithoutAUsableValue()
    {
        var pool = WeeklyPool.Of(Accounts, Now, unavailable: false);
        Assert.Equal(44.5, pool.Remaining);
        Assert.Equal(4, pool.Counted);
        Assert.Equal(178, pool.Sum);
        Assert.Equal(Tone.Healthy, pool.Tone);
        Assert.Equal(new[] { "보조" }, pool.NeedLogin);
        Assert.Equal(new[] { "새 계정" }, pool.Unknown);
        // A late account still counts with its last value and is named in the tooltip.
        Assert.Equal(new[] { "연구" }, pool.Previous);
    }

    [Fact]
    public void TheTaskbarShowsTheAccountInUsesFiveHoursAndEveryAccountsWeekTogether()
    {
        var usage = TaskbarUsage.Of(Settings(), Accounts, activeId: 2, Now, unavailable: false)!;
        // The taskbar keeps to the numbers; the reset countdown is in the quick view and the app.
        Assert.Equal(new[] { new TaskbarPart("5시간", "9%", Tone.Critical), new TaskbarPart("전체 주간", "45%", Tone.Healthy) }, usage.Parts);
        Assert.Equal("업무 · 사용 중", usage.Title);
        Assert.Equal("9% 남음", usage.Rows[0].Value);
        Assert.Equal("4개 계정 합산, 계정 1.8개만큼", usage.Rows[1].Detail);
        Assert.Contains($"가장 먼저 초기화: 실험 · {QuotaFormat.Date(Now + SoonestReset)} (2일 5시간 후)", usage.Notes);
        Assert.Contains("재로그인이 필요해 빠진 계정: 보조", usage.Notes);
        Assert.Contains("아직 조회되지 않아 빠진 계정: 새 계정", usage.Notes);
        Assert.Contains("이전 수치로 더한 계정: 연구", usage.Notes);
        Assert.EndsWith("클릭하면 빠른 보기", usage.Notes[^1]);
        Assert.Equal("Claude SyncBar 사용량, 5시간 9% 남음, 전체 주간 45% 남음", usage.Spoken);
    }

    [Fact]
    public void TheWeeklyTimerCountsDownToTheSoonestResetOfAnAccountInTheSum()
    {
        var pool = WeeklyPool.Of(Accounts, Now, unavailable: false);
        Assert.Equal(Now + SoonestReset, pool.NextReset);
        Assert.Equal("실험", pool.NextResetAccount);
        Assert.Equal("2일 5시간 후 주간 초기화", pool.Countdown(Now));
        // An hour later the same account is an hour closer.
        var later = Now + TimeSpan.FromHours(1);
        Assert.Equal("2일 4시간 후 주간 초기화", WeeklyPool.Of(Accounts, later, unavailable: false).Countdown(later));
    }

    [Fact]
    public void AWeeklyResetSaysTheHourEvenInItsShortForm()
    {
        var reset = Now + TimeSpan.FromDays(3);
        var quota = QuotaFormat.Of(new JsonObject { ["remainingPercent"] = 40.0, ["resetsAt"] = reset.ToString("O") }, "weekly", Now);
        Assert.Equal($"{reset.ToLocalTime():MM.dd HH:mm} 초기화", quota.Short);
        Assert.Equal($"{QuotaFormat.Date(reset)} 초기화", quota.Caption);
    }

    [Fact]
    public void AResetAlreadyPastOrOfAnAccountLeftOutStartsNoTimer()
    {
        JsonObject[] accounts =
        [
            Account(1, "개인", 50, 40, weeklyReset: TimeSpan.FromHours(-1)),
            Account(5, "보조", 10, 10, needsLogin: true, weeklyReset: TimeSpan.FromHours(1)),
        ];
        var pool = WeeklyPool.Of(accounts, Now, unavailable: false);
        // Past its reset the week is full again, and when it next resets is not known until it is used.
        Assert.Equal(100, pool.Remaining);
        Assert.Null(pool.NextReset);
        Assert.Null(pool.Countdown(Now));
    }

    [Fact]
    public void TheResetClockSaysTodayTomorrowOrTheDate()
    {
        var noon = new DateTimeOffset(2030, 1, 9, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2030, 1, 9, 12, 0, 0)));
        Assert.Equal("15:00", QuotaFormat.Clock(noon + TimeSpan.FromHours(3), noon));
        Assert.Equal("내일 08:00", QuotaFormat.Clock(noon + TimeSpan.FromHours(20), noon));
        Assert.Equal(QuotaFormat.Date(noon + TimeSpan.FromDays(3)), QuotaFormat.Clock(noon + TimeSpan.FromDays(3), noon));
    }

    [Fact]
    public void WithoutTheServiceEveryValueIsAPreviousOne()
    {
        var usage = TaskbarUsage.Of(Settings(), Accounts, activeId: 2, Now, unavailable: true)!;
        Assert.All(usage.Parts, part => Assert.Equal(Tone.Muted, part.Tone));
        Assert.Equal("업무 · 마지막 적용 계정", usage.Title);
        Assert.StartsWith("서비스에 연결하지 못했어요", usage.Notes[^1]);
    }

    [Fact]
    public void TheReadoutFollowsTheDisplaySettings()
    {
        Assert.Null(TaskbarUsage.Of(Settings(taskbar: false), Accounts, 2, Now, false));
        Assert.Null(TaskbarUsage.Of(Settings(fiveHour: false, weekly: false), Accounts, 2, Now, false));
        Assert.Equal(new[] { "전체 주간" }, TaskbarUsage.Of(Settings(fiveHour: false), Accounts, 2, Now, false)!.Parts.Select(part => part.Label));
        Assert.Equal(new[] { "5시간" }, TaskbarUsage.Of(Settings(weekly: false), Accounts, 2, Now, false)!.Parts.Select(part => part.Label));
        // Settings from before the readout existed show it.
        Assert.NotNull(TaskbarUsage.Of(new JsonObject { ["claudeFiveHour"] = true }, Accounts, 2, Now, false));
    }

    [Fact]
    public void AnAccountThatNeedsSigningInAgainSaysSoInsteadOfANumber()
    {
        var usage = TaskbarUsage.Of(Settings(), Accounts, activeId: 5, Now, unavailable: false)!;
        Assert.Equal(new TaskbarPart("5시간", "재로그인 필요", Tone.Warning), usage.Parts[0]);
        Assert.Equal("45%", usage.Parts[1].Value);
        Assert.Equal("Claude SyncBar 사용량, 5시간 재로그인 필요, 전체 주간 45% 남음", usage.Spoken);
    }

    [Fact]
    public void WithoutAnAccountInUseOrAnyWeeklyValueTheNumbersAreDashes()
    {
        var usage = TaskbarUsage.Of(Settings(), [Account(6, "새 계정", null, null)], activeId: null, Now, unavailable: false)!;
        Assert.Equal(new[] { new TaskbarPart("5시간", "—", Tone.Muted), new TaskbarPart("전체 주간", "—", Tone.Muted) }, usage.Parts);
        Assert.Equal("적용된 계정 없음", usage.Title);
        Assert.Equal("합산할 계정이 없어요", usage.Rows[1].Detail);
        Assert.Equal("Claude SyncBar 사용량, 5시간 정보 없음, 전체 주간 정보 없음", usage.Spoken);
    }

    [Fact]
    public void AFiveHourQuotaPastItsResetIsFullAgain()
    {
        var usage = TaskbarUsage.Of(Settings(), [Account(1, "개인", 3, 50, fiveHourReset: TimeSpan.FromMinutes(-5))], activeId: 1, Now, false)!;
        Assert.Equal(new TaskbarPart("5시간", "100%", Tone.Healthy), usage.Parts[0]);
        Assert.Equal("초기화됨", usage.Rows[0].Detail);
    }
}
