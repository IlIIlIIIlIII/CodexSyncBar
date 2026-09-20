using System.Globalization;

namespace CodexSyncBar.Windows.Core;

public sealed record ResetCreditExpiryGroup(
    int Count,
    DateTimeOffset ExpiresAt,
    string RemainingText,
    string ExpiresAtText);

public static class UsageFormatting
{
    private static readonly string[] KoreanWeekdays = ["일요일", "월요일", "화요일", "수요일", "목요일", "금요일", "토요일"];

    public static string QuotaResetDate(DateTimeOffset? date)
    {
        if (date is null) return "초기화 날짜 미확인";
        var local = date.Value.ToLocalTime();
        return $"{local.ToString("MM.dd", CultureInfo.InvariantCulture)} ({KoreanWeekdays[(int)local.DayOfWeek][0]}) {local.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }

    public static string QuotaResetDescription(DateTimeOffset? date, DateTimeOffset? relativeTo = null) =>
        date is null ? ResetDescription(null) : $"{ResetDescription(date, relativeTo)}\n{QuotaResetDate(date)}";

    public static IReadOnlyList<ResetCreditExpiryGroup> ResetCreditExpiryGroups(
        IEnumerable<DateTimeOffset> expirations,
        DateTimeOffset? relativeTo = null)
    {
        if (expirations is null)
        {
            return [];
        }

        var now = relativeTo ?? DateTimeOffset.UtcNow;
        return expirations
            .GroupBy(value => value.ToUniversalTime())
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var local = group.Key.ToLocalTime();
                var date = local.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
                var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
                return new ResetCreditExpiryGroup(
                    group.Count(),
                    local,
                    ResetCreditRemainingText(group.Key - now),
                    $"{date} ({KoreanWeekdays[(int)local.DayOfWeek]}) {time}");
            })
            .ToArray();
    }

    private static string ResetCreditRemainingText(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return "만료됨";
        }

        var totalMinutes = interval.Ticks / TimeSpan.TicksPerMinute;
        if (totalMinutes == 0)
        {
            return "1분 미만 남음";
        }

        var days = totalMinutes / 1_440;
        var hours = totalMinutes % 1_440 / 60;
        var minutes = totalMinutes % 60;
        if (days > 0)
        {
            return FormattableString.Invariant($"{days}일 {hours}시간 {minutes}분 남음");
        }

        return hours > 0
            ? FormattableString.Invariant($"{hours}시간 {minutes}분 남음")
            : FormattableString.Invariant($"{minutes}분 남음");
    }

    public static string ResetDescription(
        DateTimeOffset? date,
        DateTimeOffset? relativeTo = null)
    {
        if (date is null)
        {
            return "초기화 시각 미확인";
        }

        var interval = date.Value - (relativeTo ?? DateTimeOffset.UtcNow);
        if (interval <= TimeSpan.Zero)
        {
            return "곧 초기화";
        }

        var totalMinutes = Math.Max(0, (int)interval.TotalMinutes);
        var days = totalMinutes / 1_440;
        var hours = totalMinutes % 1_440 / 60;
        var minutes = totalMinutes % 60;
        if (days > 0)
        {
            return $"{days}일 {hours}시간 후 초기화";
        }

        if (hours > 0)
        {
            return $"{hours}시간 {minutes}분 후 초기화";
        }

        return $"{Math.Max(1, minutes)}분 후 초기화";
    }

    public static string ResetCreditExpiryDescription(
        DateTimeOffset date,
        DateTimeOffset? relativeTo = null)
    {
        var interval = date - (relativeTo ?? DateTimeOffset.UtcNow);
        if (interval <= TimeSpan.Zero)
        {
            return "만료됨";
        }

        var totalMinutes = Math.Max(1, (int)interval.TotalMinutes);
        if (interval <= TimeSpan.FromHours(24))
        {
            return $"{totalMinutes / 60}시간 {totalMinutes % 60}분";
        }

        var days = totalMinutes / 1_440;
        var hours = totalMinutes % 1_440 / 60;
        return $"{days}일 {hours}시간";
    }

    public static string? CompactResetCreditExpiryDescription(
        IEnumerable<DateTimeOffset> expirations,
        DateTimeOffset? relativeTo = null)
    {
        var sorted = expirations.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
        {
            return null;
        }

        var next = ResetCreditExpiryDescription(sorted[0], relativeTo);
        var remaining = sorted.Length - 1;
        return remaining > 0
            ? $"다음 만료 {next} · 외 {remaining}회"
            : $"다음 만료 {next}";
    }
}
