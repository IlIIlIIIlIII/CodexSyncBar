using System.Globalization;
using System.Text.Json.Nodes;

namespace ClaudeSyncBar.App.Model;

/// <summary>
/// The weekly timeline's words, shared by its page and the quick view's strip. Amounts are whole percents
/// of one account's week, and an amount left is said as the usage page says it; times are local, and the
/// inner edges of a planned segment are said to the hour.
/// </summary>
public static class TimelineText
{
    private static readonly string[] Days = ["일", "월", "화", "수", "목", "금", "토"];
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);
    // A view read longer ago than this no longer says where now is.
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<JsonObject> Items(JsonNode? list) => list is JsonArray array ? array.OfType<JsonObject>().ToArray() : [];

    /// <summary>"53": a whole number, never below zero.</summary>
    public static string Whole(double value) =>
        Math.Max(0, Math.Round(double.IsFinite(value) ? value : 0, MidpointRounding.AwayFromZero)).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>"53%", or a dash when the amount is not known.</summary>
    public static string Percent(double? value) => value is { } known ? Whole(known) + "%" : "—";

    /// <summary>
    /// An amount left, as the usage page says it: "0%" only when nothing is left and "100%" only when nothing
    /// is used, so a sliver reads "0.6%" and 99.6 reads "99%".
    /// </summary>
    public static string Left(double? value) => QuotaFormat.Percent(value is { } known ? Math.Clamp(known, 0, 100) : null);

    /// <summary>"72 → 0.6%": what was left of an account as a hold began and as it ended.</summary>
    public static string LeftRange(double from, double to) => $"{Left(from)[..^1]} → {Left(to)}";

    public static string Weekday(DateTimeOffset moment) => Days[(int)moment.ToLocalTime().DayOfWeek];

    /// <summary>"금 15:00".</summary>
    public static string DayTime(DateTimeOffset moment) => $"{Weekday(moment)} {moment.ToLocalTime():HH:mm}";

    /// <summary>The nearest full hour of the local clock.</summary>
    public static DateTimeOffset Hour(DateTimeOffset moment)
    {
        var shifted = moment.ToLocalTime().AddMinutes(30);
        return shifted.AddTicks(-(shifted.Ticks % TimeSpan.TicksPerHour));
    }

    /// <summary>"수 05:00": a moment to the nearest hour.</summary>
    public static string DayHour(DateTimeOffset moment) => DayTime(Hour(moment));

    /// <summary>
    /// A segment's edge as it is shown. An edge that is a reset, the week's own start or end or the segment
    /// account's reset, keeps its minutes; any other is said to the nearest hour, but never to one past that
    /// account's reset, which it would then seem to cross.
    /// </summary>
    public static DateTimeOffset Edge(JsonNode? view, int? profileId, DateTimeOffset edge)
    {
        var reset = Items(view?["segments"]).Where(segment => AppState.Int(segment["profileId"]) == profileId)
            .Select(segment => QuotaFormat.Time(segment["resetsAt"])).FirstOrDefault();
        if (QuotaFormat.Time(view?["startsAt"]) is { } starts && Apart(edge, starts, weekly: true) <= TimeSpan.FromMinutes(1)) return edge;
        if (reset is { } at && Apart(edge, at, weekly: false) <= TimeSpan.FromMinutes(1)) return at;
        var hour = Hour(edge);
        if (reset is { } limit && edge <= limit && hour > limit) hour = hour.AddHours(-1);
        return hour;
    }

    // How far apart two moments are; weekly, how far from the same time of the week.
    private static TimeSpan Apart(DateTimeOffset first, DateTimeOffset second, bool weekly)
    {
        var ticks = (first - second).Duration().Ticks;
        return TimeSpan.FromTicks(weekly ? Math.Min(ticks % Week.Ticks, Week.Ticks - ticks % Week.Ticks) : ticks);
    }

    /// <summary>
    /// "일 18:00", or "11.13 (금) 20:30" for an edge on a day the header's range leaves out, where the
    /// weekday alone would name the day a week later.
    /// </summary>
    public static string EdgeText(JsonNode? view, int? profileId, DateTimeOffset edge)
    {
        var shown = Edge(view, profileId, edge);
        var days = Items(view?["days"]);
        if (days.Count == 0) return DayTime(shown);
        var date = shown.ToLocalTime().Date;
        return date < Middle(days[0]).Date || date > Middle(days[^1]).Date ? QuotaFormat.Date(shown) : DayTime(shown);
    }

    // A day of the timeline runs from the week's starting hour to the same hour of the next day:
    // it is named by the date its middle falls on.
    private static DateTimeOffset Middle(JsonNode? day) => (QuotaFormat.Time(day?["startsAt"]) ?? default).AddHours(12).ToLocalTime();

    /// <summary>"월 9".</summary>
    public static string DayLabel(JsonNode? day) => $"{Days[(int)Middle(day).DayOfWeek]} {Middle(day).Day}";

    /// <summary>"월".</summary>
    public static string DayLetter(JsonNode? day) => Days[(int)Middle(day).DayOfWeek];

    /// <summary>"11.09 (월)".</summary>
    public static string DayDate(JsonNode? day) => $"{Middle(day):MM.dd} ({Days[(int)Middle(day).DayOfWeek]})";

    /// <summary>Today by the date the day is named by, so the bold label is today's date.</summary>
    public static bool IsToday(JsonNode? day, DateTimeOffset now) => Middle(day).Date == now.ToLocalTime().Date;

    /// <summary>"11.09 (월) ~ 11.15 (일)": the first and the last day shown.</summary>
    public static string Range(JsonNode? view)
    {
        var days = Items(view?["days"]);
        return days.Count == 0 ? "" : $"{DayDate(days[0])} ~ {DayDate(days[^1])}";
    }

    /// <summary>
    /// Whether a view still says where now is: read within the last few minutes, of a week not over yet.
    /// An older one would show an old now line and an old turn as if they were current.
    /// </summary>
    public static bool Current(JsonNode? view, DateTimeOffset now) =>
        QuotaFormat.Time(view?["now"]) is { } read && now - read <= Fresh && QuotaFormat.Time(view?["endsAt"]) is { } ends && ends > now;

    /// <summary>"예요" after a name that ends in a vowel, "이에요" after one that ends in a consonant.</summary>
    public static string Copula(string name)
    {
        var trimmed = name.TrimEnd();
        var last = trimmed.Length > 0 ? char.ToLowerInvariant(trimmed[^1]) : ' ';
        // Latin letters read as a final consonant when they end the word as ㄴ, ㄹ, ㅁ or ㅂ do ("Main", "Lab", ".com");
        // others are read with a vowel after them ("Max", "Work").
        var closed = last is >= '가' and <= '힣' ? (last - '가') % 28 != 0 : last is '0' or '1' or '3' or '6' or '7' or '8' or 'l' or 'm' or 'n' or 'b' or 'p';
        return closed ? "이에요" : "예요";
    }

    private static string Name(JsonNode? item) => AppState.Str(item?["name"]) ?? "계정";

    /// <summary>Whose turn it is by the plan, or that no account has any of the user's share left.</summary>
    public static string TurnTitle(JsonNode? view) =>
        view?["summary"]?["turn"] is JsonObject turn ? $"지금은 {Name(turn)} 차례예요" : "지금 쓸 수 있는 내 몫이 없어요";

    /// <summary>The page's line under the turn: when it resets, what is left, and what keeping to the plan would leave unused.</summary>
    public static string TurnCaption(JsonNode? view, DateTimeOffset now)
    {
        if (view?["summary"]?["turn"] is not JsonObject turn)
        {
            var first = Items(view?["resets"]).FirstOrDefault(reset => QuotaFormat.Flag(reset["upcoming"]));
            return first is not null && QuotaFormat.Time(first["resetsAt"]) is { } at
                ? $"가장 먼저 초기화되는 계정: {Name(first)} · {QuotaFormat.Clock(at, now)}" + (QuotaFormat.Flag(first["projected"]) ? " 예상" : "") : "";
        }

        var left = Left(QuotaFormat.Number(turn["mineLeft"]));
        var daily = Percent(QuotaFormat.Number(view["dailyShare"]));
        string caption;
        if (QuotaFormat.Time(turn["resetsAt"]) is not { } resets) caption = $"{left}가 남았어요. 초기화 시각은 한 번 써야 정해져요.";
        else if (QuotaFormat.Number(turn["forecastLost"]) is >= 1 and var lost)
            caption = $"{QuotaFormat.Clock(resets, now)}에 초기화돼요. {left}가 남았고, 계획대로 하루 {daily}씩 써도 {Percent(lost)}쯤은 사라져요.";
        else caption = $"{QuotaFormat.Clock(resets, now)}에 초기화돼요. {left}가 남았고, 계획대로 하루 {daily}씩 쓰면 그 전에 다 써요.";
        if (!QuotaFormat.Flag(turn["isCurrent"]) && Items(view["accounts"]).FirstOrDefault(row => QuotaFormat.Flag(row["isCurrent"])) is { } current)
            caption += $" 지금 사용 중인 계정은 {Name(current)}{Copula(Name(current))}.";
        return caption;
    }

    /// <summary>The quick view's line under the turn; empty when no account has the turn.</summary>
    public static string StripCaption(JsonNode? view)
    {
        if (view?["summary"]?["turn"] is not JsonObject turn) return "";
        if (QuotaFormat.Time(turn["resetsAt"]) is null) return "초기화 시각은 한 번 써야 정해져요";
        return QuotaFormat.Number(turn["forecastLost"]) is >= 1 and var lost
            ? $"계획대로 하루 {Percent(QuotaFormat.Number(view["dailyShare"]))}씩 써도 {Percent(lost)}쯤 사라져요"
            : "계획대로 쓰면 초기화 전에 다 써요";
    }

    /// <summary>The quick view's count: how much the planned accounts used together against the plan so far.</summary>
    public static string StripCount(JsonNode? view) =>
        $"{Items(view?["accounts"]).Count(row => QuotaFormat.Flag(row["planned"]))}개 계정 합쳐 {Percent(QuotaFormat.Number(view?["summary"]?["used"]) ?? 0)} 씀"
        + $" · 지금까지 계획 {Percent(QuotaFormat.Number(view?["summary"]?["plannedSoFar"]) ?? 0)}";

    /// <summary>The empty plan's second line: what would bring a plan, by why the accounts were left out.</summary>
    public static string EmptyCaption(JsonNode? view)
    {
        var reasons = Items(view?["accounts"]).Select(row => AppState.Str(row["excluded"])).ToHashSet();
        if (!reasons.Contains("share")) return "계정을 추가하거나 다시 로그인하면 여기에 일주일 계획이 보여요.";
        return reasons.Contains("login") ? "내 몫을 1% 이상으로 정하거나 다시 로그인하면 여기에 일주일 계획이 보여요." : "내 몫을 1% 이상으로 정하면 여기에 일주일 계획이 보여요.";
    }

    /// <summary>
    /// What a reset took or will take: the day of one still ahead, and for one passed whether the account
    /// ran dry, went over the user's share or left some of it unused. Empty when nothing is known of it.
    /// </summary>
    public static string ResetStatus(JsonNode? reset, DateTimeOffset now)
    {
        if (QuotaFormat.Flag(reset?["upcoming"]))
        {
            if (QuotaFormat.Time(reset?["resetsAt"]) is not { } at) return "";
            var days = (at.ToLocalTime().Date - now.ToLocalTime().Date).Days;
            return (days <= 0 ? "오늘" : days == 1 ? "내일" : Weekday(at)) + (QuotaFormat.Flag(reset?["projected"]) ? " 예상" : "");
        }

        if (QuotaFormat.Flag(reset?["spent"])) return "다 씀";
        if (QuotaFormat.Number(reset?["over"]) is >= 1 and var over) return $"내 몫 {Percent(over)} 넘김";
        return QuotaFormat.Number(reset?["left"]) switch
        {
            >= 1 and var left => $"{Percent(left)} 사라짐",
            not null => "남김 없음",
            null => "",
        };
    }

    private static IEnumerable<JsonObject> Passed(JsonNode? view) => Items(view?["resets"]).Where(reset => !QuotaFormat.Flag(reset["upcoming"]));

    /// <summary>
    /// The lost amount: what the passed resets left of the user's share, counting only what a reset's own
    /// label counts, a percent or more. A dash when no passed reset tells what it left.
    /// </summary>
    public static string Lost(JsonNode? view)
    {
        var passed = Passed(view).ToList();
        if (passed.Count > 0 && passed.All(reset => QuotaFormat.Number(reset["left"]) is null)) return Percent(null);
        return Percent(passed.Select(reset => QuotaFormat.Number(reset["left"])).Where(left => left >= 1).Sum(left => left!.Value));
    }

    /// <summary>"개인 계정 · 11.15 (일) 16:00 초기화 때 외 1곳": the reset that left the most of the user's share unused.</summary>
    public static string LostCaption(JsonNode? view)
    {
        var passed = Passed(view).ToList();
        if (passed.Count == 0) return "아직 지난 초기화가 없어요";
        if (passed.All(reset => QuotaFormat.Number(reset["left"]) is null)) return "초기화 때 남은 양을 읽지 못했어요";
        var lost = passed.Where(reset => QuotaFormat.Number(reset["left"]) is >= 1).OrderByDescending(reset => QuotaFormat.Number(reset["left"])).ToList();
        if (lost.Count == 0 || QuotaFormat.Time(lost[0]["resetsAt"]) is not { } at) return "초기화 때 남긴 몫이 없어요";
        return $"{Name(lost[0])} · {QuotaFormat.Date(at)} 초기화 때" + (lost.Count > 1 ? $" 외 {lost.Count - 1}곳" : "");
    }

    /// <summary>
    /// The usage page's status of an account whose weekly value is an earlier one ("갱신 지연 · 이전 수치",
    /// "조회 실패 · 이전 수치"); null while the value is fresh or there is none.
    /// </summary>
    public static string? Stale(JsonNode? account, DateTimeOffset now, bool unavailable)
    {
        var usage = QuotaFormat.Account(account, ["weekly"], now, unavailable);
        return usage.Get("weekly") is { Remaining: not null, Tone: Tone.Muted } ? usage.Status : null;
    }
}
