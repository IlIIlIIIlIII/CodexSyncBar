using System.Globalization;
using System.Text.Json.Nodes;

namespace ClaudeSyncBar.App.Model;

/// <summary>How much of a quota is left, as the app shows it: never the accent, always with a number.</summary>
public enum Tone { Healthy, Warning, Critical, Muted }

public sealed record Quota(string Key, string Title, double? Remaining, Tone Tone, string Caption, string Short)
{
    public string Value => QuotaFormat.Percent(Remaining);

    /// <summary>What a screen reader says for the meter.</summary>
    public string Spoken => Remaining is null ? $"{Title} 정보 없음" : $"{Title} {Value} 남음, {Caption}";
}

public sealed record AccountUsage(IReadOnlyList<Quota> Quotas, string Status, bool StatusIsWarning, DateTimeOffset? Updated)
{
    public Quota? Get(string key) => Quotas.FirstOrDefault(quota => quota.Key == key);
}

/// <summary>Five-hour and weekly quota presentation; the same rules as the Ubuntu app's quota.py.</summary>
public static class QuotaFormat
{
    public static readonly (string Key, string Title, string Setting)[] Quotas =
        [("fiveHour", "5시간", "claudeFiveHour"), ("weekly", "주간", "claudeWeekly")];

    // Older than this, a value is shown as the previous one.
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);
    private static readonly string[] Days = ["일", "월", "화", "수", "목", "금", "토"];

    public static string Title(string key) => Quotas.First(quota => quota.Key == key).Title;

    /// <summary>The quotas the user chose to see, in display order.</summary>
    public static IReadOnlyList<string> Visible(JsonNode? settings) =>
        Quotas.Where(quota => settings?[quota.Setting]?.GetValueKind() != System.Text.Json.JsonValueKind.False).Select(quota => quota.Key).ToArray();

    public static double? RemainingPercent(JsonNode? window)
    {
        if (window is not JsonObject value) return null;
        double? remaining = Number(value["remainingPercent"]);
        if (remaining is null && Number(value["usedPercent"]) is { } used) remaining = 100 - used;
        return remaining is { } r && double.IsFinite(r) ? Math.Clamp(r, 0, 100) : null;
    }

    /// <summary>"0%" only when nothing is left and "100%" only when nothing is used; a sliver shows as such.</summary>
    public static string Percent(double? remaining)
    {
        if (remaining is not { } value) return "—";
        var sliver = Math.Ceiling(Math.Round(value * 10, 6)) / 10;
        if (value > 0 && sliver < 1) return sliver.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        if (value is > 99 and < 100) return "99%";
        return Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    public static Tone ToneOf(double remaining) => remaining <= 10 ? Tone.Critical : remaining <= 25 ? Tone.Warning : Tone.Healthy;

    public static string Until(DateTimeOffset moment, DateTimeOffset now)
    {
        var minutes = Math.Max(0, (int)Math.Ceiling((moment - now).TotalMinutes));
        var (days, rest) = Math.DivRem(minutes, 24 * 60);
        var (hours, mins) = Math.DivRem(rest, 60);
        if (days > 0) return hours > 0 ? $"{days}일 {hours}시간 후" : $"{days}일 후";
        if (hours > 0 && mins > 0) return $"{hours}시간 {mins}분 후";
        if (hours > 0) return $"{hours}시간 후";
        return mins > 0 ? $"{mins}분 후" : "곧";
    }

    /// <summary>"10.22 (목) 10:00" in local time.</summary>
    public static string Date(DateTimeOffset moment)
    {
        var local = moment.ToLocalTime();
        return $"{local:MM.dd} ({Days[(int)local.DayOfWeek]}) {local:HH:mm}";
    }

    /// <summary>"05:00" today, "내일 05:00" tomorrow, else "10.22 (목) 05:00", in local time.</summary>
    public static string Clock(DateTimeOffset moment, DateTimeOffset now)
    {
        var local = moment.ToLocalTime();
        var today = now.ToLocalTime().Date;
        if (local.Date == today) return $"{local:HH:mm}";
        return local.Date == today.AddDays(1) ? $"내일 {local:HH:mm}" : Date(moment);
    }

    public static Quota Of(JsonNode? window, string key, DateTimeOffset now, bool previous = false)
    {
        var title = Title(key);
        var remaining = RemainingPercent(window);
        if (remaining is null) return new(key, title, null, Tone.Muted, "정보 없음", "정보 없음");
        var resets = Time(window?["resetsAt"]);
        // Past its reset time the whole quota is available again until it is next used.
        if (resets is { } reset && reset <= now && !previous) return new(key, title, 100, Tone.Healthy, "초기화됨", "초기화됨");
        var tone = previous ? Tone.Muted : ToneOf(remaining.Value);
        if (resets is null)
        {
            var caption = remaining >= 100 ? "사용 전" : "초기화 시각 미정";
            return new(key, title, remaining, tone, caption, caption);
        }

        var local = resets.Value.ToLocalTime();
        if (key == "fiveHour")
        {
            var clock = $"{local:HH:mm} 초기화";
            return new(key, title, remaining, tone, previous ? clock : $"{Until(resets.Value, now)} 초기화 · {local:HH:mm}", clock);
        }

        // The short form still says the hour: a week's reset falls at any time of day.
        return new(key, title, remaining, tone, $"{Date(resets.Value)} 초기화", $"{local:MM.dd HH:mm} 초기화");
    }

    public static AccountUsage Account(JsonNode? account, IReadOnlyList<string> keys, DateTimeOffset now, bool unavailable = false, bool refreshing = false)
    {
        var usage = account?["usage"] as JsonObject;
        var updated = Time(usage?["updatedAt"]);
        var known = keys.Any(key => RemainingPercent(usage?[key]) is not null);
        var reason = "";
        if (unavailable) reason = "연결 실패";
        else if (Flag(account?["needsLogin"]) || Flag(account?["isPending"])) reason = "재로그인 필요";
        else if (usage?["error"] is not null && usage["error"]!.GetValueKind() == System.Text.Json.JsonValueKind.String) reason = "조회 실패";
        else if (refreshing) reason = "갱신 중";
        else if (known && (updated is null || now - updated > Stale)) reason = "갱신 지연";
        var status = reason + (reason.Length > 0 && known ? " · 이전 수치" : "");
        if (reason.Length == 0 && usage is null) status = "아직 조회되지 않음";
        var warning = reason is not ("" or "갱신 중" or "갱신 지연");
        var quotas = keys.Select(key => Of(usage?[key], key, now, previous: reason.Length > 0)).ToArray();
        return new(quotas, status, warning, updated);
    }

    public static DateTimeOffset? Time(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? time : null;

    public static bool Flag(JsonNode? value) => value is JsonValue v && v.TryGetValue<bool>(out var flag) && flag;

    public static double? Number(JsonNode? value) => value is JsonValue v && v.TryGetValue<double>(out var number) ? number : null;
}
