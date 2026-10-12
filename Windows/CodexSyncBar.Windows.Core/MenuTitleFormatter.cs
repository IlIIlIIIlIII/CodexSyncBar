namespace CodexSyncBar.Windows.Core;

public static class MenuTitleFormatter
{
    public static string Title(
        AccountProfile profile,
        UsageSnapshot? snapshot,
        string? failure,
        IReadOnlyList<UsageDisplayItem> items,
        bool isRefreshing,
        bool hasDeviceMismatch,
        DateTimeOffset? now = null)
    {
        var label = profile.ShortName;
        var selectedItems = items.Distinct().Take(MenuBarUsagePreferences.MaximumItemCount).ToArray();
        if (profile.NeedsLogin)
        {
            return $"{label} 🔒";
        }

        if (!string.IsNullOrWhiteSpace(failure))
        {
            return $"{label} ⚠{(hasDeviceMismatch ? " !" : string.Empty)}";
        }

        if (selectedItems.Length == 0)
        {
            return $"{label}{(hasDeviceMismatch ? " !" : string.Empty)}";
        }

        if (isRefreshing && snapshot is null)
        {
            return $"{label} ···";
        }

        if (snapshot is null)
        {
            return $"{label} —";
        }

        var reference = now ?? DateTimeOffset.UtcNow;
        if (reference - snapshot.UpdatedAt > TimeSpan.FromMinutes(10))
        {
            return $"{label} — ⏱{(hasDeviceMismatch ? " !" : string.Empty)}";
        }

        var fragments = selectedItems
            .Select(item => item.RemainingPercent(snapshot) is { } remaining
                ? $"{remaining}%"
                : "—")
            .ToArray();
        var needsWarning = selectedItems
            .Select(item => item.RemainingPercent(snapshot))
            .OfType<int>()
            .Any(remaining => remaining <= 10);
        var prefix = needsWarning ? "⚠ " : string.Empty;
        return $"{prefix}{label} {string.Join(" · ", fragments)}{(hasDeviceMismatch ? " !" : string.Empty)}";
    }
}
