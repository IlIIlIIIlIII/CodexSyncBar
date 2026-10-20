using ClaudeSyncBar.Ubuntu.Backend;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.App.Model;

/// <summary>Made-up accounts for --demo, relative to now, covering every state the screens show.</summary>
public static class DemoData
{
    public static DemoService Create()
    {
        var now = DateTimeOffset.UtcNow;
        DashboardUsage Usage(double fiveUsed, TimeSpan? fiveReset, double weekUsed, TimeSpan weekReset, TimeSpan? age = null) => new(
            new(weekUsed, now + weekReset, 604800), now - (age ?? TimeSpan.FromMinutes(2)),
            new(fiveUsed, fiveReset is { } reset ? now + reset : null, 18000));
        return new DemoService(
        [
            new(1, "main@example.com", "개인 계정", false, false, Usage(28, new(1, 42, 0), 32, new(1, 19, 12, 0)), "Max 20x"),
            new(2, "work@example.com", "업무 계정", false, false, Usage(91, new(0, 36, 0), 76, new(4, 2, 24, 0)), "Max 20x"),
            new(3, "lab@example.com", "실험 계정", false, false, Usage(0, null, 95, new(0, 16, 48, 0)), "Max 5x"),
            new(4, "research@example.com", "연구 계정", false, false, Usage(46, new(0, 22, 0), 19, new(4, 18, 12, 0), TimeSpan.FromMinutes(40)), "Pro"),
            new(5, "sub@example.com", "보조 계정", false, true, null, "Pro"),
        ], activeProfileId: 2);
    }
}
