using System.Text.Json.Nodes;

namespace ClaudeSyncBar.App.Model;

/// <summary>
/// Every account's weekly quota taken together: what is left of them all, each
/// account counting the same, since plans differ and only percentages are known.
/// Accounts that need signing in again or were never looked up are left out.
/// <paramref name="NextReset"/> is the soonest weekly reset among those counted, when the sum next grows.
/// </summary>
public sealed record WeeklyPool(double? Remaining, Tone Tone, int Counted, double Sum,
    IReadOnlyList<string> NeedLogin, IReadOnlyList<string> Unknown, IReadOnlyList<string> Previous,
    DateTimeOffset? NextReset = null, string? NextResetAccount = null)
{
    public const string Label = "전체 주간";

    public static WeeklyPool Of(IReadOnlyList<JsonObject> accounts, DateTimeOffset now, bool unavailable)
    {
        var needLogin = new List<string>();
        var unknown = new List<string>();
        var previous = new List<string>();
        var sum = 0.0;
        var counted = 0;
        DateTimeOffset? nextReset = null;
        string? nextAccount = null;
        foreach (var account in accounts)
        {
            var name = AppState.AccountName(account);
            if (AppState.NeedsLogin(account))
            {
                needLogin.Add(name);
                continue;
            }

            var quota = QuotaFormat.Account(account, ["weekly"], now, unavailable).Quotas[0];
            if (quota.Remaining is not { } remaining)
            {
                unknown.Add(name);
                continue;
            }

            sum += remaining;
            counted++;
            if (quota.Tone == Tone.Muted) previous.Add(name);
            // A reset already past is full again until next used; its next reset is not known yet.
            if (QuotaFormat.Time(account["usage"]?["weekly"]?["resetsAt"]) is { } reset && reset > now && (nextReset is null || reset < nextReset))
            {
                nextReset = reset;
                nextAccount = name;
            }
        }

        double? pool = counted == 0 ? null : sum / counted;
        // Grey only when every value is a previous one; a single late account is named instead.
        var tone = pool is not { } value ? Tone.Muted : previous.Count == counted ? Tone.Muted : QuotaFormat.ToneOf(value);
        return new(pool, tone, counted, sum, needLogin, unknown, previous, nextReset, nextAccount);
    }

    /// <summary>"16시간 48분 후 주간 초기화": the countdown to the soonest reset, or null when none is known.</summary>
    public string? Countdown(DateTimeOffset now) => NextReset is { } next ? $"{QuotaFormat.Until(next, now)} 주간 초기화" : null;
}
