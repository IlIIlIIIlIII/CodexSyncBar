using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSyncBar.App.Model;

/// <summary>One "label value" pair on the taskbar.</summary>
public sealed record TaskbarPart(string Label, string Value, Tone Tone);

/// <summary>A tooltip line: a quota, how much of it is left and when or from what.</summary>
public sealed record TaskbarRow(string Label, string Value, Tone Tone, string Detail);

/// <summary>
/// What the taskbar shows: the five-hour quota of the account in use and the
/// weekly quota of every account together, and the tooltip that explains both.
/// </summary>
public sealed record TaskbarUsage(IReadOnlyList<TaskbarPart> Parts, string Title, IReadOnlyList<TaskbarRow> Rows,
    IReadOnlyList<string> Notes, string Spoken)
{
    public const string FiveHourLabel = "5시간", WeeklyLabel = WeeklyPool.Label;

    /// <summary>Null when the user turned the taskbar off or chose to see no quota at all.</summary>
    public static TaskbarUsage? Of(JsonNode? settings, IReadOnlyList<JsonObject> accounts, int? activeId, DateTimeOffset now, bool unavailable)
    {
        if (settings?["taskbarUsage"]?.GetValueKind() == JsonValueKind.False) return null;
        var keys = QuotaFormat.Visible(settings);
        if (keys.Count == 0) return null;
        var active = activeId is null ? null : accounts.FirstOrDefault(account => AppState.Int(account["id"]) == activeId);
        var parts = new List<TaskbarPart>();
        var rows = new List<TaskbarRow>();
        var notes = new List<string>();

        if (keys.Contains("fiveHour"))
        {
            if (active is null)
            {
                parts.Add(new(FiveHourLabel, "—", Tone.Muted));
                rows.Add(new(FiveHourLabel, "정보 없음", Tone.Muted, "적용된 계정이 없어요"));
            }
            else if (AppState.NeedsLogin(active))
            {
                parts.Add(new(FiveHourLabel, "재로그인 필요", Tone.Warning));
                rows.Add(new(FiveHourLabel, "재로그인 필요", Tone.Warning, ""));
            }
            else
            {
                var usage = QuotaFormat.Account(active, ["fiveHour"], now, unavailable);
                var quota = usage.Quotas[0];
                var known = quota.Remaining is not null;
                parts.Add(new(FiveHourLabel, known ? quota.Value : "—", quota.Tone));
                rows.Add(new(FiveHourLabel, known ? quota.Value + " 남음" : "정보 없음", quota.Tone, known ? quota.Caption : ""));
                if (usage.Status.Length > 0) notes.Add($"{AppState.AccountName(active)} · {usage.Status}");
            }
        }

        if (keys.Contains("weekly"))
        {
            var pool = WeeklyPool.Of(accounts, now, unavailable);
            parts.Add(new(WeeklyLabel, pool.Remaining is { } left ? QuotaFormat.Percent(left) : "—", pool.Tone));
            var detail = pool.Counted switch
            {
                0 => "합산할 계정이 없어요",
                1 => "1개 계정 기준",
                _ => $"{pool.Counted}개 계정 합산, 계정 {(pool.Sum / 100).ToString("0.#", CultureInfo.InvariantCulture)}개만큼",
            };
            rows.Add(new(WeeklyLabel, pool.Remaining is { } remaining ? QuotaFormat.Percent(remaining) + " 남음" : "정보 없음", pool.Tone, detail));
            if (pool.NextReset is { } soonest)
                notes.Add($"가장 먼저 초기화: {pool.NextResetAccount} · {QuotaFormat.Date(soonest)} ({QuotaFormat.Until(soonest, now)})");
            if (pool.NeedLogin.Count > 0) notes.Add("재로그인이 필요해 빠진 계정: " + Names(pool.NeedLogin));
            if (pool.Unknown.Count > 0) notes.Add("아직 조회되지 않아 빠진 계정: " + Names(pool.Unknown));
            if (pool.Previous.Count > 0) notes.Add("이전 수치로 더한 계정: " + Names(pool.Previous));
        }

        var updated = accounts.Select(account => QuotaFormat.Time(account["usage"]?["updatedAt"])).OfType<DateTimeOffset>().DefaultIfEmpty().Max();
        var footer = unavailable ? "서비스에 연결하지 못했어요" : updated == default ? "아직 조회되지 않았어요" : $"{updated.ToLocalTime():HH:mm} 갱신";
        notes.Add(footer + " · 클릭하면 빠른 보기");
        var title = active is null ? "적용된 계정 없음" : $"{AppState.AccountName(active)} · {(unavailable ? "마지막 적용 계정" : "사용 중")}";
        var spoken = "Claude SyncBar 사용량, " + string.Join(", ", parts.Select(part =>
            part.Value == "—" ? $"{part.Label} 정보 없음" : part.Value.EndsWith('%') ? $"{part.Label} {part.Value} 남음" : $"{part.Label} {part.Value}"));
        return new(parts, title, rows, notes, spoken);
    }

    private static string Names(IReadOnlyList<string> names) =>
        names.Count <= 2 ? string.Join(", ", names) : $"{names[0]}, {names[1]} 외 {names.Count - 2}개";
}
