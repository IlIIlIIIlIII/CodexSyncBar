using System.Diagnostics;
using System.Text.Json;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Ubuntu.Backend;

/// <summary>What the last automatic switch did, shown in the app.</summary>
public sealed record AutoSwitchResult(DateTimeOffset At, int? FromProfileId, int? ToProfileId, string Message, bool Succeeded);

public sealed class AutoSwitchSettings
{
    public bool Enabled { get; set; }
    public AutoSwitchResult? Last { get; set; }
    // Continuing sessions a usage limit stopped, once the account in use has room again.
    public bool ResumeEnabled { get; set; }
    public AutoSwitchResult? ResumeLast { get; set; }
    public Dictionary<string, ResumeRecord> Resumed { get; set; } = [];
}

/// <summary>How stopped sessions are found, checked and continued; tests stand in for the real ones.</summary>
internal sealed record ResumeHooks(
    Func<DateTimeOffset, IReadOnlyList<StoppedSession>> Find,
    Func<StoppedSession, bool> StillOpen,
    Func<StoppedSession, bool> Launch)
{
    public static ResumeHooks For(WindowsPaths paths) =>
        new(now => AutoResume.Find(paths, now), AutoResume.StillOpen, session => AutoResume.Launch(session, paths));
}

/// <summary>
/// Which accounts the smart switch may choose, where the user set it on the
/// account list. An account not set here follows its plan.
/// </summary>
public sealed class SmartSwitchSettings
{
    public Dictionary<int, bool> Candidates { get; set; } = [];

    public bool? Chosen(int profileId) => Candidates.TryGetValue(profileId, out var chosen) ? chosen : null;
}

internal sealed class AutoSwitchStore(WindowsPaths paths) : SettingsFile<AutoSwitchSettings>(paths, "auto-switch.json", "자동 전환 설정 파일");

// Its own file: the automatic switch rewrites auto-switch.json long after reading it.
internal sealed class SmartSwitchStore(WindowsPaths paths) : SettingsFile<SmartSwitchSettings>(paths, "smart-switch.json", "스마트 전환 설정 파일");

internal class SettingsFile<T>(WindowsPaths paths, string name, string description) where T : new()
{
    private static readonly JsonSerializerOptions Format = new(Wire.Json) { WriteIndented = true };
    private string FilePath => Path.Combine(paths.StateRoot, name);

    public T Load()
    {
        WindowsPathSafety.EnsureFile(FilePath, description);
        if (!File.Exists(FilePath)) return new();
        try { return JsonSerializer.Deserialize<T>(File.ReadAllBytes(FilePath), Format) ?? new(); }
        // An unreadable file leaves the defaults, such as automatic switching off, rather than guessing.
        catch (JsonException) { return new(); }
    }

    public void Save(T settings)
    {
        WindowsPathSafety.EnsureFile(FilePath, description);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WindowsPathSafety.WritePrivateBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(settings, Format));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

/// <summary>
/// The smart switch: among its candidates (the accounts ticked on the account
/// list, at first the $200 Max ones) with some of the weekly and more than a
/// quarter of the five-hour quota left, the one whose week resets first, so
/// the quota that would lapse soonest is used first.
/// </summary>
internal static class SmartSwitch
{
    internal const string Plan = "Max 20x";
    // An account with 75% or more of its five hours used is passed over.
    internal const double FiveHourMinimum = 25;
    private static readonly string[] Days = ["일", "월", "화", "수", "목", "금", "토"];

    internal sealed record Choice(AccountView Account, double FiveHour, double Weekly, DateTimeOffset? WeeklyReset);

    /// <summary>Whether the smart switch may choose an account: as the user ticked it, else only on the $200 Max plan.</summary>
    internal static bool IsCandidate(string? plan, bool? chosen) => chosen ?? plan == Plan;

    /// <summary>The best account, which can be the one in use; null when none qualifies.</summary>
    internal static Choice? Choose(IEnumerable<AccountView> accounts, DateTimeOffset now) => accounts
        .Where(account => account.SmartCandidate && !account.IsPending && !account.NeedsLogin)
        .Select(account => (Account: account,
            FiveHour: AutoSwitchPolicy.TrustedRemaining(account.Usage?.FiveHour, account.Usage, now),
            Weekly: AutoSwitchPolicy.TrustedRemaining(account.Usage?.Weekly, account.Usage, now),
            // A week past its reset is whole again, but when it next resets is not known until it is used.
            Reset: account.Usage?.Weekly?.ResetsAt is { } reset && reset > now ? reset : (DateTimeOffset?)null))
        // Any of the week is enough, as for the automatic switch; the five hours need more than a quarter left.
        .Where(item => item.FiveHour > FiveHourMinimum && item.Weekly is > 0)
        // The soonest weekly reset first, an unknown one last; then the most weekly left, the most five-hour left, the priority order.
        .Select((item, order) => (item.Account, FiveHour: item.FiveHour!.Value, Weekly: item.Weekly!.Value, item.Reset, Order: order))
        .OrderBy(item => item.Reset ?? DateTimeOffset.MaxValue).ThenByDescending(item => item.Weekly)
        .ThenByDescending(item => item.FiveHour).ThenBy(item => item.Order)
        .Select(item => new Choice(item.Account, item.FiveHour, item.Weekly, item.Reset))
        .FirstOrDefault();

    /// <summary>"10.22 (목) 10:00" in local time, as the apps write a weekly reset.</summary>
    internal static string When(DateTimeOffset moment)
    {
        var local = moment.ToLocalTime();
        return $"{local:MM.dd} ({Days[(int)local.DayOfWeek]}) {local:HH:mm}";
    }

    /// <summary>Why the choice was made, as the confirmation shows it.</summary>
    internal static string Reason(string weekly, string fiveHour, DateTimeOffset? reset) =>
        $"주간 {weekly} · 5시간 {fiveHour} 남음" + (reset is { } next ? $" · 주간 초기화 {When(next)}" : "");

    internal const string NoneMessage = "스마트 전환 후보 중 주간 한도가 남고 5시간 한도를 75% 미만 쓴 계정이 없습니다.";
    internal const string NoCandidateMessage = "스마트 전환 후보로 고른 계정이 없습니다. 계정 목록에서 후보를 골라 주세요.";
}

/// <summary>
/// When the account in use has nothing left, and which account comes next.
/// Only fresh usage without errors is acted on: an account is never left, or
/// chosen, on a value that may no longer be true.
/// </summary>
internal static class AutoSwitchPolicy
{
    internal static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(15);
    // Only a quota with nothing at all left is used up; 0.2% left is still room.
    private static bool Empty(double remaining) => remaining <= 0;
    // From here the account in use is looked at every minute instead of every five.
    internal const double Low = 10;

    // Past its reset time a quota is whole again until it is next used.
    internal static double? Remaining(UsageWindow? window, DateTimeOffset now) =>
        window is null ? null : window.ResetsAt is { } reset && reset <= now ? 100 : window.RemainingPercent;

    private static bool Trusted(DashboardUsage? usage, DateTimeOffset now) =>
        usage is not null && usage.Error is null && usage.UpdatedAt > now - FreshFor;

    /// <summary>The quota the account has used up, or null when it has some left or this is not known well enough.</summary>
    internal static string? UsedUp(DashboardUsage? usage, DateTimeOffset now)
    {
        if (!Trusted(usage, now)) return null;
        if (Remaining(usage!.FiveHour, now) is { } fiveHour && Empty(fiveHour)) return "5시간";
        if (Remaining(usage.Weekly, now) is { } weekly && Empty(weekly)) return "주간";
        return null;
    }

    internal static double? Lowest(DashboardUsage? usage, DateTimeOffset now)
    {
        if (usage is null) return null;
        var known = new[] { Remaining(usage.FiveHour, now), Remaining(usage.Weekly, now) }.OfType<double>().ToArray();
        return known.Length == 0 ? null : known.Min();
    }

    internal static bool HasRoom(DashboardUsage? usage, DateTimeOffset now) =>
        Trusted(usage, now) && Remaining(usage!.Weekly, now) is { } weekly && !Empty(weekly)
        && (Remaining(usage.FiveHour, now) is not { } fiveHour || !Empty(fiveHour));

    /// <summary>The first account in priority order, other than the one in use, that can be used now.</summary>
    internal static int? NextAccount(IEnumerable<AccountView> accounts, int active, DateTimeOffset now) =>
        accounts.FirstOrDefault(account => account.Id != active && !account.IsPending && !account.NeedsLogin && HasRoom(account.Usage, now))?.Id;

    /// <summary>The quota left, or null when it is not known well enough to act on.</summary>
    internal static double? TrustedRemaining(UsageWindow? window, DashboardUsage? usage, DateTimeOffset now) =>
        Trusted(usage, now) ? Remaining(window, now) : null;

    /// <summary>A desktop notification; the service runs without a window of its own.</summary>
    internal static void Notify(string message) => BackendPlatform.Notify(message);
}
