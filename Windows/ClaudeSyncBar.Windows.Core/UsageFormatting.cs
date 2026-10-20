using System.Globalization;

namespace ClaudeSyncBar.Windows.Core;

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
}
