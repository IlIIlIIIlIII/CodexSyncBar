using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexSyncBar.Windows.Core;

public sealed record WidgetAction(string Verb, int? ProfileId, string? ConfigurationRevision, string RequestId);

public static class WidgetTemplates
{
    public const string DefinitionId = "CodexSyncBar.Usage";
    public const string ProviderClassId = "D946A8F4-089F-4FD3-AF58-372F5033415D";

    public static string Render(DashboardSnapshot snapshot, bool large, string? notice = null, bool busy = false)
    {
        var applied = snapshot.Accounts.FirstOrDefault(a => a.ProfileId == snapshot.ActiveProfileId);
        var usage = applied?.Usage;
        var candidates = snapshot.Accounts.Where(a => !a.NeedsLogin).ToArray();
        var isBusy = busy || snapshot.IsBusy;
        var status = notice ?? snapshot.Error ?? usage?.Error ?? snapshot.Operation?.Message;
        var hasStatus = !string.IsNullOrWhiteSpace(status);
        var body = new JsonArray();
        var actions = new JsonArray();

        if (candidates.Length == 0)
        {
            body.Add(Text("계정을 연결하세요", "Large", "Bolder", maxLines: 2));
            body.Add(Text("로그인하면 남은 사용량과 계정을 여기서 확인할 수 있어요.", subtle: true, maxLines: 3, spacing: "Medium"));
            body.Add(Meters(null, large));
            actions.Add(Action("앱에서 로그인", "settings", snapshot.ConfigurationRevision));
        }
        else
        {
            var identity = Text(applied is null ? "적용된 계정 없음" : $"현재 · {SafeLabel(applied.DisplayName)}", weight: "Bolder", maxLines: 1);
            if (large)
            {
                body.Add(new JsonObject
                {
                    ["type"] = "ColumnSet", ["columns"] = new JsonArray(
                        new JsonObject { ["type"] = "Column", ["width"] = "stretch", ["verticalContentAlignment"] = "Center",
                            ["items"] = new JsonArray(identity, Text(applied is null ? "계정을 선택하세요" : SafeLabel(applied.MaskedEmail, 70), "Small", subtle: true, maxLines: 1)) },
                        new JsonObject { ["type"] = "Column", ["width"] = "80px", ["spacing"] = "Small",
                            ["items"] = new JsonArray(new JsonObject { ["type"] = "ActionSet", ["actions"] = new JsonArray(Action("앱 열기", "settings", snapshot.ConfigurationRevision)) }) })
                });
            }
            else body.Add(identity);
            var meters = Meters(usage, large);
            if (large)
                body.Add(new JsonObject { ["type"] = "Container", ["style"] = "emphasis", ["spacing"] = "Small", ["items"] = new JsonArray(meters) });
            else body.Add(meters);

            var updated = usage?.UpdatedAt == DateTimeOffset.MinValue ? null : usage?.UpdatedAt;
            var timestamp = updated is null ? "갱신 정보 없음" : $"{updated.Value.ToLocalTime():MM/dd HH:mm} 갱신";
            var fresh = updated is not null && DateTimeOffset.UtcNow - updated.Value <= TimeSpan.FromMinutes(5);
            var summary = isBusy ? "계정 적용 중" : hasStatus ? StatusSummary(snapshot, notice, usage)
                : fresh ? "최신 사용량" : "마지막 확인 값";
            body.Add(Text($"{timestamp} · {summary}", "Small", subtle: !hasStatus && !isBusy, maxLines: 1,
                spacing: "Small", color: isBusy ? "Accent" : hasStatus
                    ? snapshot.Operation?.State == "completed" && snapshot.Error is null && usage?.Error is null && notice is null ? "Good" : "Warning"
                    : "Default"));

            if (large)
            {
                body.Add(Credits(usage));
                body.Add(Devices(snapshot));
            }

            if (isBusy)
            {
                body.Add(Text("모든 장치에 적용하고 있어요", weight: "Bolder", maxLines: 1, spacing: "Medium"));
                body.Add(Text("완료 상태를 확인할 때까지 잠시 기다려 주세요.", "Small", subtle: true, maxLines: 2, spacing: "Small"));
                actions.Add(Action("앱에서 상태 확인", "settings", snapshot.ConfigurationRevision));
            }
            else
            {
                var choices = new JsonArray(candidates.Select(account => (JsonNode)new JsonObject
                {
                    ["title"] = SafeLabel(account.DisplayName),
                    ["value"] = account.ProfileId.ToString(CultureInfo.InvariantCulture),
                }).ToArray());
                body.Add(new JsonObject
                {
                    ["type"] = "Input.ChoiceSet", ["id"] = "profileId", ["label"] = "적용할 계정",
                    ["style"] = "compact", ["isMultiSelect"] = false, ["choices"] = choices,
                    ["value"] = (candidates.FirstOrDefault(a => a.ProfileId == snapshot.ActiveProfileId) ?? candidates[0]).ProfileId.ToString(CultureInfo.InvariantCulture),
                    ["spacing"] = "Small", ["separator"] = large,
                });
                actions.Add(Action("모두 적용", "apply", snapshot.ConfigurationRevision, includeInputs: true));
                actions.Add(Action("새로고침", "refresh", snapshot.ConfigurationRevision));

            }
        }

        return new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard", ["version"] = "1.5", ["lang"] = "ko",
            ["body"] = body, ["actions"] = actions,
        }.ToJsonString();
    }

    private static JsonObject Meters(DashboardUsage? usage, bool large)
    {
        var columns = new JsonArray();
        // A missing five-hour window means the API did not report that limit;
        // do not imply a second quota exists by drawing an empty placeholder.
        if (usage?.Session is { } session) columns.Add(Meter("5시간 남음", session, large));
        columns.Add(Meter("주간 남음", usage?.Weekly, large, "Accent"));
        return new JsonObject { ["type"] = "ColumnSet", ["spacing"] = "Small", ["columns"] = columns };
    }

    private static JsonObject Meter(string label, UsageWindow? window, bool large, string availableColor = "Good")
    {
        var remaining = window is null || !double.IsFinite(window.UsedPercent)
            ? (double?)null : Math.Clamp(100 - window.UsedPercent, 0, 100);
        var value = remaining is null ? "—" : $"{remaining:0}%";
        var items = new JsonArray
        {
            Text(label, "Small", subtle: true, maxLines: 1),
            Text(value, large ? "ExtraLarge" : "Medium", "Bolder", maxLines: 1,
                color: remaining is null ? "Default" : remaining <= 20 ? "Warning" : availableColor),
        };
        items.Add(Text(UsageFormatting.ResetDescription(window?.ResetsAt), "Small", subtle: true, maxLines: 1, spacing: "Small"));
        if (window?.ResetsAt is { } reset)
            items.Add(Text(UsageFormatting.QuotaResetDate(reset), "Small", subtle: true, maxLines: 1));
        return new JsonObject { ["type"] = "Column", ["width"] = "stretch", ["spacing"] = "Small", ["items"] = items };
    }

    private static JsonObject Credits(DashboardUsage? usage)
    {
        var now = DateTimeOffset.UtcNow;
        var groups = UsageFormatting.ResetCreditExpiryGroups(usage?.ResetCreditExpirations ?? [], now);
        var items = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "ColumnSet",
                ["columns"] = new JsonArray(
                    new JsonObject { ["type"] = "Column", ["width"] = "stretch", ["verticalContentAlignment"] = "Center",
                        ["items"] = new JsonArray(Text("초기화권", weight: "Bolder", maxLines: 1)) },
                    new JsonObject { ["type"] = "Column", ["width"] = "auto", ["spacing"] = "Small",
                        ["items"] = new JsonArray(Text(usage?.ResetCredits is int count ? $"{count}개" : "수량 미확인",
                            usage?.ResetCredits is null ? "Small" : "Large", "Bolder", maxLines: 1,
                            color: usage?.ResetCredits > 0 ? "Good" : "Default")) }),
            },
        };
        if (usage?.ResetCredits == 0)
            items.Add(Text("사용 가능한 초기화권이 없어요.", "Small", subtle: true, maxLines: 1, spacing: "Small"));
        else if (groups.Count == 0)
            items.Add(Text("만료 정보 없음", "Small", subtle: true, maxLines: 1, spacing: "Small"));
        else
        {
            foreach (var group in groups.Take(2))
            {
                items.Add(Text($"{group.Count}개 · {group.RemainingText}", weight: "Bolder", maxLines: 1,
                    spacing: "Small", color: group.ExpiresAt - now <= TimeSpan.FromDays(1) ? "Warning" : "Default"));
                items.Add(Text(group.ExpiresAtText, "Small", subtle: true, maxLines: 1));
            }
            if (groups.Count > 2)
                items.Add(Text($"외 {groups.Count - 2}개 만료 일정 · 앱에서 전체 보기", "Small", subtle: true, maxLines: 1, spacing: "Small"));
        }
        return new JsonObject { ["type"] = "Container", ["style"] = "emphasis", ["spacing"] = "Medium", ["items"] = items };
    }

    private static JsonObject Devices(DashboardSnapshot snapshot)
    {
        var total = snapshot.Devices.Count;
        var applied = snapshot.Devices.Count(d => d.IsReachable && d.ProfileId is not null && d.ProfileId == snapshot.ActiveProfileId);
        var offline = snapshot.Devices.Count(d => !d.IsReachable);
        var other = total - applied - offline;
        var details = total == 0 ? "앱에서 장치를 연결하세요."
            : string.Join(" · ", snapshot.Devices.GroupBy(d => d.Kind).OrderBy(g => g.Key switch { "windows" => 0, "wsl" => 1, "ssh" => 2, _ => 3 }).Select(g =>
                $"{g.Key switch { "windows" => "Windows PC", "wsl" => "WSL", "ssh" => "SSH", _ => "장치" }} {g.Count()}대"));
        var result = total == 0 ? "장치 상태 확인 중" : offline + other == 0 ? "모든 장치 연결됨" : $"오프라인 {offline} · 다른 계정 {other}";
        return new JsonObject
        {
            ["type"] = "Container", ["separator"] = false, ["spacing"] = "Medium",
            ["items"] = new JsonArray(Text($"장치 적용 · {applied}/{total} 적용됨", weight: "Bolder", maxLines: 1,
                    color: total == 0 ? "Default" : offline + other == 0 ? "Good" : "Warning"),
                Text(details, "Small", subtle: true, maxLines: 1, spacing: "Small"),
                Text(result, "Small", subtle: true, maxLines: 1)),
        };
    }

    private static string StatusSummary(DashboardSnapshot snapshot, string? notice, DashboardUsage? usage)
    {
        if (snapshot.Operation?.State == "recoveryRequired") return "복구 확인 필요";
        if (snapshot.Operation?.State == "failed") return "전환 결과 확인 필요";
        if (!string.IsNullOrWhiteSpace(snapshot.Error) || !string.IsNullOrWhiteSpace(usage?.Error)) return "연결 확인 필요";
        if (snapshot.Operation?.State == "completed") return "계정 적용 완료";
        return notice is null ? "상태 확인 필요" : "알림 · 새로고침해 주세요";
    }

    public static WidgetAction ParseAction(string verb, string data)
    {
        if (verb is not ("apply" or "refresh" or "settings") || data.Length > DashboardPipeProtocol.MaximumRequestBytes)
            throw new InvalidDataException("지원하지 않는 위젯 동작입니다.");
        using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("requestId", out var requestIdElement)
            || requestIdElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("위젯 요청 식별자가 없습니다.");
        var requestId = requestIdElement.GetString();
        if (!Guid.TryParseExact(requestId, "N", out _)) throw new InvalidDataException("잘못된 위젯 요청 식별자입니다.");
        int? profileId = null;
        string? revision = null;
        if (verb == "apply")
        {
            if (!root.TryGetProperty("profileId", out var profile) || profile.ValueKind != JsonValueKind.String
                || !int.TryParse(profile.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId) || parsedId <= 0
                || !root.TryGetProperty("configurationRevision", out var revisionElement) || revisionElement.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("전환할 계정과 장치 구성을 다시 확인해 주세요.");
            revision = revisionElement.GetString();
            if (revision is not { Length: > 0 and <= 128 }) throw new InvalidDataException("잘못된 장치 구성입니다.");
            profileId = parsedId;
        }
        return new(verb, profileId, revision, requestId!);
    }

    private static JsonObject Text(string text, string size = "Default", string weight = "Default", bool subtle = false, int? maxLines = null, string spacing = "None", string color = "Default")
    {
        var block = new JsonObject
        {
            ["type"] = "TextBlock", ["text"] = text, ["size"] = size, ["wrap"] = true,
            ["weight"] = weight, ["isSubtle"] = subtle, ["spacing"] = spacing, ["color"] = color,
        };
        if (maxLines is not null) block["maxLines"] = maxLines;
        return block;
    }

    private static JsonObject Action(string title, string verb, string revision, bool includeInputs = false) => new()
    {
        ["type"] = "Action.Execute", ["title"] = title, ["verb"] = verb,
        ["tooltip"] = verb == "apply" ? "선택한 계정을 모든 장치에 적용" : title,
        ["style"] = verb == "apply" ? "positive" : "default",
        ["associatedInputs"] = includeInputs ? "auto" : "none",
        ["data"] = new JsonObject { ["configurationRevision"] = revision, ["requestId"] = Guid.NewGuid().ToString("N") },
    };

    private static string SafeLabel(string text, int length = 70)
    {
        var clean = new string(text.Where(c => !char.IsControl(c)).Take(length).ToArray());
        // Provider-side fallback for older controllers that accidentally use the email as alias.
        if (clean.Contains('@'))
        {
            var at = clean.IndexOf('@');
            return at > 0 ? $"{clean[0]}***{clean[at..]}" : "계정";
        }
        return clean;
    }
}
