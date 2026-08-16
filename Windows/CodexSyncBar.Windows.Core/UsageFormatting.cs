namespace CodexSyncBar.Windows.Core;

public static class UsageFormatting
{
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
