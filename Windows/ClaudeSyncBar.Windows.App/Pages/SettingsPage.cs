using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeSyncBar.App.Controls;
using ClaudeSyncBar.App.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClaudeSyncBar.App.Pages;

/// <summary>What the app shows, how it runs, automatic switching and the weekly message. Every change applies at once.</summary>
public sealed class SettingsPage : SyncPage
{
    protected override void Render()
    {
        var settings = State.Settings as JsonObject;
        var busy = State.Busy || State.RefreshRequested;
        Header("설정", null);
        ErrorBar();

        Group("표시");
        var display = Rows();
        display.Children.Add(Ui.SettingRow(Glyphs.Stopwatch, "5시간 사용량 표시", "계정별 5시간 한도 잔여량을 앱, 트레이, 작업 표시줄에 표시해요.",
            Switch(settings, "claudeFiveHour", "5시간 사용량 표시", fallback: true)));
        display.Children.Add(Ui.SettingRow(Glyphs.Calendar, "주간 사용량 표시", "계정별 주간 한도 잔여량을 앱, 트레이, 작업 표시줄에 표시해요.",
            Switch(settings, "claudeWeekly", "주간 사용량 표시", fallback: true)));
        display.Children.Add(Ui.Expander(Ui.SettingRow(Glyphs.Chart, "작업 표시줄에 사용량 표시",
                "지금 계정의 5시간 한도와 모든 계정의 주간 한도 합계를 작업 표시줄 맨 왼쪽에 한 줄로 보여 줘요.",
                Switch(settings, "taskbarUsage", "작업 표시줄에 사용량 표시", fallback: true)),
            [Ui.SubRow(null, Ui.Caption("전체 주간은 재로그인이 필요한 계정을 빼고, 계정마다 같은 비중으로 남은 양을 더한 값이에요. 플랜마다 실제 한도 크기는 달라요. 위에서 끈 한도는 작업 표시줄에도 나오지 않아요."), 56)]));

        Group("실행");
        var run = Rows();
        run.Children.Add(Ui.SettingRow(Glyphs.Power, "Windows에 로그인하면 시작", "창을 열지 않고 트레이에서 시작해요.",
            Switch(settings, "launchAtLogin", "Windows에 로그인하면 시작")));
        var quit = Ui.Button("완전히 종료", click: async (_, _) =>
        {
            if (await Flows.ConfirmAsync(XamlRoot, "Claude SyncBar를 완전히 종료할까요?",
                "트레이 아이콘이 사라지고 사용량 갱신, 자동 전환, 주간 자동 메시지도 멈춰요. 진행 중인 전환은 마친 뒤 종료해요.", "종료"))
                App.Current!.Quit();
        });
        run.Children.Add(Ui.SettingRow(Glyphs.Window, "창을 닫아도 트레이에서 실행",
            "사용량 갱신과 진행 중인 전환은 멈추지 않아요. 앱을 끝내려면 완전히 종료하세요.", quit));

        Group("자동 전환");
        var automatic = Rows();
        automatic.Children.Add(Ui.Expander(Ui.SettingRow(Glyphs.Lightning, "사용량이 바닥나면 자동 전환",
                "지금 계정의 5시간이나 주간 한도가 0%가 되면, 우선순위에서 지금 쓸 수 있는 첫 계정으로 모든 활성 장치를 전환해요. 5분마다, 10% 이하로 남으면 1분마다 확인해요.",
                Switch(settings, "autoSwitchEnabled", "사용량이 바닥나면 자동 전환")),
            [.. Last(settings?["autoSwitchLast"], "마지막 자동 전환"),
             Ui.SubRow(null, Ui.Caption("Claude 데스크톱 앱은 다시 시작되지만, 세션 목록과 설정은 그대로 이어져요. 우선순위가 더 높은 계정을 다시 쓸 수 있게 돼도 자동으로 돌아가지는 않아요."), 56)]));
        automatic.Children.Add(Ui.Expander(Ui.SettingRow(Glyphs.Play, "한도로 멈춘 세션 자동 재개",
                "지금 계정에 사용량이 다시 생기면(자동 전환이나 한도 초기화), 최근 24시간 안에 사용량 한도로 멈춘 Claude Code 세션에 “이어서 진행”을 보내 백그라운드에서 다시 실행해요.",
                Switch(settings, "autoResumeEnabled", "한도로 멈춘 세션 자동 재개")),
            [Ui.SubRow(null, Ui.Caption("권한 확인 없이 그 세션의 권한 모드대로 실행돼요. 창이나 데스크톱 앱에 열려 있는 세션은 건너뛰고, 같은 세션은 하루 3번까지만 재개해요."), 56),
             .. Last(settings?["autoResumeLast"], "마지막 자동 재개")]));
        automatic.Children.Add(Ui.Expander(Ui.SettingRow(Glyphs.List, "우선순위",
            "위에 있는 계정부터 골라요. 계정 목록 순서와 같고, 로그인이 필요하거나 남은 사용량이 없는 계정은 건너뛰어요.", null), Priority(busy)));

        Group("주간 자동 메시지", "켜진 계정은 주간 한도가 초기화되면 시작 메시지를 한 번 보내요. 새 계정은 기본으로 켜져요.");
        var weekly = Rows();
        var enabled = (settings?["weekly"]?["preferences"]?["enabledProfileIds"] as JsonArray)?.Select(id => AppState.Int(id)).ToHashSet() ?? [];
        foreach (var account in State.Accounts) weekly.Children.Add(Weekly(account, enabled, busy));
        if (State.Accounts.Count == 0) weekly.Children.Add(Ui.Card(Ui.Text("등록된 계정이 없어요.", secondary: true)));

        Group("정보");
        var about = Rows();
        var icon = new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.png")), Width = 32, Height = 32 };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var version = typeof(App).Assembly.GetName().Version;
        var text = Ui.Stack(1, false, Ui.Text($"Claude SyncBar {version?.ToString(3)}", strong: true),
            Ui.Caption("Anthropic의 공식 제품이 아닌 개인용 유틸리티예요. 공개 문서가 없는 사용량 응답 형식에 기대고 있어요."));
        if (AppState.Str(settings?["stateDirectory"]) is { Length: > 0 } directory) text.Children.Add(Ui.Caption("데이터 위치 · " + directory));
        about.Children.Add(Ui.Row(Ui.Columns(16, 1, icon, text)));
    }

    // A switch that saves its setting the moment it is flipped.
    private static ToggleSwitch Switch(JsonObject? settings, string key, string name, bool fallback = false)
    {
        var on = settings?[key] is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : fallback;
        var toggle = Ui.Toggle(on, name, async next => await State.CommandAsync("settings.save", new JsonObject { [key] = next }));
        toggle.IsEnabled = State.Error is null;
        return toggle;
    }

    // The last automatic switch or resume, and whether it went through.
    private static IEnumerable<UIElement> Last(JsonNode? last, string title)
    {
        if (AppState.Str(last?["message"]) is not { Length: > 0 } message) yield break;
        var at = QuotaFormat.Time(last?["at"]);
        var line = at is { } time ? $"{time.ToLocalTime():M월 d일 HH:mm} · {message}" : message;
        var succeeded = QuotaFormat.Flag(last?["succeeded"]);
        var text = Ui.Stack(1, false, Ui.Text(title), Ui.Caption(line));
        yield return Ui.SubRow(null, Ui.Columns(12, 1, Ui.Glyph(succeeded ? Glyphs.Completed : Glyphs.Warning, 16,
            succeeded ? "SyncBarGlyphHealthy" : "SyncBarGlyphWarning"), text), 56);
    }

    private static List<UIElement> Priority(bool busy)
    {
        var accounts = State.Accounts;
        var rows = new List<UIElement>();
        for (var i = 0; i < accounts.Count; i++)
        {
            var account = accounts[i];
            var id = AppState.Int(account["id"]) ?? 0;
            var name = AppState.AccountName(account);
            var state = AppState.NeedsLogin(account) ? "로그인 필요" : id == State.ActiveProfileId ? "사용 중" : "";
            var detail = string.Join(" · ", new[] { AppState.Str(account["email"]) is { } email && email != name ? email : null, state }.Where(part => !string.IsNullOrEmpty(part)));
            var number = Ui.Caption((i + 1).ToString(), wrap: false);
            number.Width = 16;
            var up = Ui.IconButton(Glyphs.Up, $"{name} 우선순위 위로", async (_, _) => await State.CommandAsync("account.move", new { profileId = id, offset = -1 }));
            up.IsEnabled = !busy && i > 0;
            var down = Ui.IconButton(Glyphs.Down, $"{name} 우선순위 아래로", async (_, _) => await State.CommandAsync("account.move", new { profileId = id, offset = 1 }));
            down.IsEnabled = !busy && i < accounts.Count - 1;
            rows.Add(Ui.SubRow(null, Ui.Columns(12, 1, number, Ui.Identity(account, 24, detail), up, down), 56));
        }

        if (rows.Count == 0) rows.Add(Ui.SubRow(null, Ui.Caption("등록된 계정이 없어요."), 56));
        return rows;
    }

    private Border Weekly(JsonObject account, HashSet<int?> enabled, bool busy)
    {
        var id = AppState.Int(account["id"]) ?? 0;
        var name = AppState.AccountName(account);
        var available = !AppState.NeedsLogin(account);
        var send = Ui.Button("지금 보내기", Glyphs.Send, click: async (_, _) =>
        {
            if (await Flows.ConfirmAsync(XamlRoot, "주간 시작 메시지를 보낼까요?", $"{name} 계정으로 Claude Code 메시지를 한 번 보내요.", "보내기"))
                await State.CommandAsync("weekly.send", new { profileId = id }, "주간 메시지를 보내고 있어요.");
        }, tip: "이 계정으로 Claude Code 메시지를 한 번 보내요");
        send.IsEnabled = available && !busy;
        var toggle = Ui.Toggle(enabled.Contains(id), $"{name} 주간 자동 메시지",
            async on => await State.CommandAsync("weekly.set", new { profileId = id, enabled = on }), enabled: available && !busy);
        var detail = available ? null : "로그인이 필요해요";
        return Ui.Row(Ui.Columns(16, 0, Ui.Identity(account, 32, detail), send, toggle));
    }
}
