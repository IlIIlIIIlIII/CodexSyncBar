using System.Text.Json.Nodes;
using ClaudeSyncBar.App.Controls;
using ClaudeSyncBar.App.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;

namespace ClaudeSyncBar.App.Pages;

/// <summary>One choice in the account picker.</summary>
[Bindable]
public sealed class AccountChoice
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>
/// Which account to apply and what it changes on each device. Choosing an
/// account only previews it; the bar at the bottom applies it.
/// </summary>
public sealed class SwitchPage : SyncPage
{
    private static readonly DataTemplate ChoiceTemplate = (DataTemplate)XamlReader.Load(
        """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <StackPanel Padding="0,4" Spacing="0">
                <TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                <TextBlock Text="{Binding Detail}" Style="{StaticResource CaptionTextBlockStyle}" Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextTrimming="CharacterEllipsis" />
            </StackPanel>
        </DataTemplate>
        """);

    protected override void Render()
    {
        Header("계정 전환", "적용할 계정과 장치를 확인해요. 계정을 고르기만 해서는 바뀌지 않아요.");
        ErrorBar();
        var columns = new Grid { ColumnSpacing = 24, RowSpacing = 24 };
        if (Wide)
        {
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        else
        {
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var left = AccountColumn();
        var right = Ui.Stack(16, false, UsageCard(), DevicesCard());
        if (Wide) Grid.SetColumn(right, 1);
        else Grid.SetRow(right, 1);
        columns.Children.Add(left);
        columns.Children.Add(right);
        Add(columns);
        if (StatusBar() is { } status) Add(status);
        SetFooter(ApplyBar());
    }

    private StackPanel AccountColumn()
    {
        var column = new StackPanel { Spacing = 8 };
        column.Children.Add(Ui.Text("현재 적용 · " + Ubuntu.Backend.BackendPlatform.LocalDeviceName, strong: true));
        var active = State.Active;
        var current = new Grid { ColumnSpacing = 12 };
        current.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        current.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        current.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        current.Children.Add(Ui.Avatar(AppState.Int(active?["id"]), active is null ? "?" : AppState.AccountName(active), 40));
        var names = Ui.Stack(0, false, Ui.Text(active is null ? "확인 필요" : AppState.AccountName(active), strong: true, wrap: false));
        if (AppState.Str(active?["email"]) is { Length: > 0 } email && email != AppState.AccountName(active)) names.Children.Add(Ui.Caption(email, wrap: false));
        names.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(names, 1);
        current.Children.Add(names);
        if (active is not null)
        {
            var check = Ui.Glyph(Glyphs.Check, 14, "SyncBarGlyphHealthy");
            AutomationProperties.SetName(check, "적용됨");
            Grid.SetColumn(check, 2);
            current.Children.Add(check);
        }

        var card = Ui.Card(current);
        card.Padding = new Thickness(16, 14, 16, 14);
        column.Children.Add(card);
        var arrow = Ui.Glyph(Glyphs.ArrowDown, 16, "SyncBarGlyphMuted");
        arrow.HorizontalAlignment = HorizontalAlignment.Center;
        arrow.Margin = new Thickness(0, 4, 0, 4);
        column.Children.Add(arrow);

        column.Children.Add(Ui.Text("전환할 계정", strong: true));
        var choices = State.Accounts.Where(account => AppState.Int(account["id"]) is not null).Select(account =>
        {
            var name = AppState.AccountName(account);
            var detail = string.Join(" · ", new[] { AppState.Str(account["email"]), AppState.Str(account["plan"]), AppState.NeedsLogin(account) ? "로그인 필요" : null }
                .Where(value => !string.IsNullOrEmpty(value) && value != name));
            return new AccountChoice { Id = AppState.Int(account["id"])!.Value, Name = name, Detail = detail };
        }).ToList();
        var picker = new ComboBox
        {
            ItemsSource = choices, ItemTemplate = ChoiceTemplate, HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = choices.FindIndex(choice => choice.Id == State.SelectedProfileId), IsEnabled = !State.Busy,
            PlaceholderText = "계정을 추가해 주세요",
        };
        AutomationProperties.SetName(picker, "전환할 계정");
        ToolTipService.SetToolTip(picker, "전환할 계정 선택 · 고르기만 해서는 적용되지 않아요");
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is AccountChoice choice) State.Select(choice.Id);
        };
        column.Children.Add(picker);

        var selected = State.Selected;
        if (selected is not null)
        {
            var needsLogin = AppState.NeedsLogin(selected);
            var status = Ui.Stack(6, true,
                Ui.Glyph(needsLogin ? Glyphs.Lock : Glyphs.Completed, 12, needsLogin ? "SyncBarGlyphWarning" : "SyncBarGlyphHealthy"),
                ToneCaption(needsLogin ? "로그인이 필요해요" : "인증 정상" + (AppState.Str(selected["plan"]) is { Length: > 0 } plan ? " · " + plan : ""), needsLogin));
            column.Children.Add(status);
            if (needsLogin)
            {
                var login = Ui.Button("로그인 열기", Glyphs.Contact, click: async (_, _) =>
                    await State.CommandAsync("account.login", new { profileId = State.SelectedProfileId }, "브라우저에서 로그인을 마쳐 주세요."));
                login.IsEnabled = !State.Busy;
                column.Children.Add(login);
            }
        }

        if (State.Busy && AppState.Str(State.Operation?["kind"]) == "login")
        {
            column.Children.Add(Ui.Caption("브라우저에서 로그인을 기다리고 있어요."));
            column.Children.Add(Ui.Stack(8, true,
                Ui.Button("로그인 창 다시 열기", click: async (_, _) => await State.CommandAsync("account.reopen", new { profileId = AppState.Int(State.Operation?["profileId"]) ?? State.SelectedProfileId })),
                Ui.Button("취소", click: async (_, _) => await State.CommandAsync("account.cancel"))));
        }

        var add = Ui.Button("계정 추가", Glyphs.Add, click: async (_, _) =>
        {
            if (await State.CommandAsync("account.add", success: "브라우저에서 로그인을 마쳐 주세요.") is not null) Shell.Navigate("accounts");
        });
        add.IsEnabled = !State.Busy;
        add.Margin = new Thickness(0, 8, 0, 0);
        column.Children.Add(add);
        var hint = Ui.Caption("적용 버튼을 눌러야 계정이 바뀌어요.");
        hint.Margin = new Thickness(0, 12, 0, 0);
        column.Children.Add(hint);
        return column;
    }

    private static TextBlock ToneCaption(string text, bool warning)
    {
        var caption = Ui.Caption(text);
        caption.Style = Ui.Style(warning ? "SyncBarToneWarning" : "SyncBarToneHealthy");
        caption.FontSize = 12;
        return caption;
    }

    private Border UsageCard()
    {
        var selected = State.Selected;
        var usage = selected?["usage"] as JsonObject;
        var updated = QuotaFormat.Time(usage?["updatedAt"]);
        var meta = updated is { } time ? $"{time.ToLocalTime():HH:mm} 확인" : "아직 조회되지 않음";
        if (AppState.Str(usage?["error"]) is { } error) meta += " · 갱신 실패: " + error;
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Ui.Text("선택한 계정 사용량", strong: true));
        var metaText = Ui.Caption(meta);
        Grid.SetColumn(metaText, 1);
        head.Children.Add(metaText);
        var body = Ui.Stack(12, false, head);
        var keys = State.VisibleQuotas;
        if (selected is null || keys.Count == 0)
        {
            body.Children.Add(Ui.Text(keys.Count == 0 ? "설정에서 표시할 사용량을 켜 주세요." : "계정을 골라 주세요.", secondary: true));
            return Ui.Card(body);
        }

        var now = DateTimeOffset.UtcNow;
        var account = QuotaFormat.Account(selected, keys, now);
        var tiles = new Grid { ColumnSpacing = 24 };
        var column = 0;
        foreach (var quota in account.Quotas)
        {
            tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = Ui.Stack(6, true, Ui.Glyph(quota.Key == "fiveHour" ? Glyphs.Stopwatch : Glyphs.Calendar, 14, "SyncBarGlyphMuted"), Ui.Text(quota.Title, secondary: true));
            var value = Ui.Stack(6, true, Ui.ToneText(quota.Value, quota.Tone, 28));
            if (quota.Remaining is not null)
            {
                var unit = Ui.Text("남음", secondary: true, wrap: false);
                unit.VerticalAlignment = VerticalAlignment.Bottom;
                unit.Margin = new Thickness(0, 0, 0, 6);
                value.Children.Add(unit);
            }

            if (Ui.ToneGlyph(quota.Tone) is { } glyph)
            {
                glyph.VerticalAlignment = VerticalAlignment.Center;
                value.Children.Add(glyph);
            }

            var resets = QuotaFormat.Time(usage?[quota.Key]?["resetsAt"]);
            var caption = resets is { } reset && reset > now && quota.Tone != Tone.Muted
                ? $"{QuotaFormat.Until(reset, now)} 초기화 · {QuotaFormat.Date(reset)}" : quota.Caption;
            var tile = Ui.Stack(6, false, label, value, Ui.Meter(quota), Ui.Caption(caption));
            Grid.SetColumn(tile, column++);
            tiles.Children.Add(tile);
        }

        body.Children.Add(tiles);
        if (account.Status.Length > 0) body.Children.Add(Ui.Pill(account.Status, account.StatusIsWarning ? Ui.PillKind.Caution : Ui.PillKind.Neutral));
        return Ui.Card(body);
    }

    private IReadOnlyList<JsonObject> Targets() => State.Preview?["targets"] is JsonArray targets
        ? targets.OfType<JsonObject>().ToArray()
        : State.Devices;

    private static bool Included(JsonObject device) => device["included"] is JsonValue included
        ? included.GetValue<bool>() : !(device["enabled"] is JsonValue enabled && !enabled.GetValue<bool>());

    private Border DevicesCard()
    {
        var targets = Targets();
        var included = targets.Where(Included).ToArray();
        var ssh = included.Count(device => AppState.Str(device["kind"]) == "ssh");
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Ui.Text("장치별 적용 미리보기", strong: true));
        var scope = Ui.Caption(included.Length == 0 ? "등록된 장치가 없어요" : ssh == 0 ? "이 PC" : $"이 PC · SSH 장치 {ssh}대");
        Grid.SetColumn(scope, 1);
        head.Children.Add(scope);
        var body = Ui.Stack(0, false, head);
        if (Wide)
        {
            var header = TableRow(Ui.Caption("장치"), Ui.Caption("현재"), Ui.Caption("적용 후 · 예정"));
            header.Margin = new Thickness(0, 12, 0, 0);
            body.Children.Add(header);
        }

        var operation = State.Operation;
        var results = (operation?["targets"] as JsonArray)?.OfType<JsonObject>()
            .Where(result => State.Busy || AppState.Str(operation?["state"]) == "recoveryRequired" || AppState.Str(result["state"]) == "skipped")
            .ToDictionary(result => AppState.Str(result["id"]) ?? "", result => result) ?? [];
        foreach (var device in targets)
        {
            body.Children.Add(new Border { Style = Ui.Style("SyncBarDivider") });
            body.Children.Add(DeviceRow(device, results));
        }

        var card = Ui.Card(body);
        card.Padding = new Thickness(20, 20, 20, 8);
        return card;
    }

    private Grid TableRow(UIElement device, UIElement current, UIElement target)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        if (Wide)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn((FrameworkElement)current, 1);
            Grid.SetColumn((FrameworkElement)target, 2);
        }
        else
        {
            grid.RowSpacing = 6;
            for (var i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow((FrameworkElement)current, 1);
            Grid.SetRow((FrameworkElement)target, 2);
        }

        grid.Children.Add(device);
        grid.Children.Add(current);
        grid.Children.Add(target);
        return grid;
    }

    private UIElement DeviceRow(JsonObject device, Dictionary<string, JsonObject> results)
    {
        var id = AppState.Str(device["id"]) ?? "";
        var local = AppState.Str(device["kind"]) == "local";
        var enabled = !(device["enabled"] is JsonValue e && !e.GetValue<bool>());
        var connection = !enabled ? "대상 제외" : AppState.Str(device["status"]) == "unreachable" ? "연결 실패"
            : QuotaFormat.Flag(device["isReachable"]) ? "연결됨" : "확인 필요";
        var problem = enabled && connection != "연결됨";
        var identityText = Ui.Stack(0, false, Ui.Text(AppState.Str(device["displayName"]) ?? "장치", wrap: false));
        var kind = (local ? "Windows" : "SSH") + " · " + connection;
        identityText.Children.Add(problem
            ? Ui.Stack(4, true, Ui.Glyph(Glyphs.Warning, 11, "SyncBarGlyphWarning"), ToneCaption(kind, true))
            : Ui.Caption(kind, wrap: false));
        var identity = Ui.Stack(10, true, Ui.KindTile(local ? Glyphs.Pc : Glyphs.Server), identityText);

        var currentLabel = AppState.Str(device["currentAccountLabel"]) ?? "확인 필요";
        var current = Ui.Stack(0, false, Ui.Text(Wide ? currentLabel : "현재: " + currentLabel, wrap: false));
        current.VerticalAlignment = VerticalAlignment.Center;

        var selected = State.Selected;
        var planned = !Included(device) ? "대상 제외" : AppState.Str(device["targetAccountLabel"]) ?? (selected is null ? "계정 선택" : AppState.AccountName(selected));
        var target = Ui.Stack(0, false, Ui.Text((Wide ? "" : "적용 후 · 예정: ") + planned, strong: Included(device), wrap: false));
        var detail = AppState.Str(device["detail"]) ?? "";
        if (AppState.Str(device["action"]) == "reapply") detail = "같은 계정 다시 적용";
        if (local && Included(device) && AppState.Str(State.Preview?["desktopNotice"]) is not null) detail = "CLI · 데스크톱 앱 다시 시작";
        if (QuotaFormat.Flag(device["skipOnSwitchFailure"])) detail = string.Join(" · ", new[] { detail, "전환 실패 시 건너뜀" }.Where(s => s.Length > 0));
        if (results.TryGetValue(id, out var result))
        {
            detail = StateLabel(AppState.Str(result["state"]));
            if (!State.Busy && AppState.Str(result["state"]) == "skipped") detail = "최근 전환 · " + detail;
            if (AppState.Str(result["detail"]) is { Length: > 0 } extra) detail += " · " + extra;
        }

        if (detail.Length > 0) target.Children.Add(Ui.Caption(detail));
        target.VerticalAlignment = VerticalAlignment.Center;

        var row = TableRow(identity, current, target);
        var button = new Button
        {
            Content = row, Style = Ui.Style("SubtleButtonStyle"), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 10, 0, 10),
        };
        AutomationProperties.SetName(button, $"{AppState.Str(device["displayName"])}, {kind}, 현재 {currentLabel}, 적용 후 {planned}");
        ToolTipService.SetToolTip(button, local ? "이 PC의 CLI와 데스크톱 앱 보기" : "장치 설정 열기");
        button.Click += (_, _) => Shell.Navigate(local ? "devices" : "device", local ? null : id.StartsWith("ssh:") ? id[4..] : id);
        return button;
    }

    private static string StateLabel(string? state) => state switch
    {
        "pending" => "대기", "ready" => "준비 완료", "applying" => "적용 중", "applied" => "검증 대기", "verified" => "검증 완료",
        "restored" => "복구됨", "skipped" => "건너뜀", "failed" => "실패", "recoveryRequired" => "복구 필요", _ => state ?? "",
    };

    private UIElement? StatusBar()
    {
        var operation = State.Operation;
        var operationState = AppState.Str(operation?["state"]);
        var recovery = operationState == "recoveryRequired";
        var skipped = (operation?["targets"] as JsonArray)?.OfType<JsonObject>().Any(target => AppState.Str(target["state"]) == "skipped") == true;
        var message = State.Busy || recovery || skipped || operationState == "failed" ? AppState.Str(operation?["message"]) : null;
        message ??= AppState.Str(State.Preview?["blockingReason"]);
        if (string.IsNullOrEmpty(message)) return null;
        var bar = new InfoBar
        {
            IsOpen = true, IsClosable = false, Message = message,
            Severity = recovery || operationState == "failed" ? InfoBarSeverity.Error : State.Busy ? InfoBarSeverity.Informational : InfoBarSeverity.Warning,
        };
        if (recovery)
            bar.ActionButton = Ui.Button("복구 다시 시도", Glyphs.Refresh, click: async (_, _) => await State.CommandAsync("recovery.retry"));
        return bar;
    }

    private Border ApplyBar()
    {
        var active = State.Active;
        var selected = State.Selected;
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (selected is not null)
        {
            var pair = Ui.Stack(6, true, Ui.Avatar(AppState.Int(active?["id"]), active is null ? "?" : AppState.AccountName(active), 32),
                Ui.Glyph(Glyphs.ArrowRight, 14, "SyncBarGlyphMuted"), Ui.Avatar(AppState.Int(selected["id"]), AppState.AccountName(selected), 32));
            pair.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAccessibilityView(pair, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            grid.Children.Add(pair);
        }

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var title = Ui.Stack(10, true, Ui.Text(selected is null ? "계정을 추가해 주세요." : $"{(active is null ? "확인 필요" : AppState.AccountName(active))} → {AppState.AccountName(selected)}", 18, strong: true, wrap: false));
        if (selected is not null && active is not null)
            title.Children.Add(Ui.Pill(AppState.Int(selected["id"]) == State.ActiveProfileId ? "다시 적용" : "적용 예정", Ui.PillKind.Accent));
        AutomationProperties.SetLiveSetting(title, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        text.Children.Add(title);
        var included = Targets().Count(Included);
        var scope = included == 0 ? "계정과 장치를 확인해 주세요." : $"활성 장치 {included}대에 적용해요.";
        if (!State.Busy && AppState.Str(State.Preview?["desktopNotice"]) is { } notice) scope += " " + notice;
        else if (included > 0) scope += " 실행 중인 Claude Code 세션은 다시 시작해야 새 계정을 써요.";
        var scopeText = Ui.Caption(scope);
        scopeText.MaxLines = 3;
        text.Children.Add(scopeText);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        var waitingLogin = State.Busy && AppState.Str(State.Operation?["kind"]) == "login";
        if (State.Busy && !waitingLogin) actions.Children.Add(Keep("ring", () => new ProgressRing { IsActive = true, Width = 20, Height = 20 }));
        var apply = Keep("apply", () =>
        {
            var button = Ui.Button("모든 장치에 적용", Glyphs.Sync, accent: true, click: async (_, _) => await State.ApplyAsync(),
                tip: "모든 장치에 적용 (Ctrl+Enter)");
            button.MinHeight = 40;
            button.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Enter, Modifiers = global::Windows.System.VirtualKeyModifiers.Control });
            return button;
        });
        Ui.Relabel(apply, State.Busy ? State.BusyLabel : "모든 장치에 적용");
        apply.IsEnabled = State.CanApply;
        actions.Children.Add(apply);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return new Border { Style = Ui.Style("SyncBarApplyBarStyle"), Child = grid };
    }
}
