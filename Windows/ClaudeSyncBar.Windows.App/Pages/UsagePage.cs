using System.Text.Json.Nodes;
using ClaudeSyncBar.App.Controls;
using ClaudeSyncBar.App.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ClaudeSyncBar.App.Pages;

/// <summary>Every account's five-hour and weekly quota side by side. Choosing a row only opens its switch preview.</summary>
public sealed class UsagePage : SyncPage
{
    protected override void Render()
    {
        var keys = State.VisibleQuotas;
        var busy = State.Busy || State.RefreshRequested;
        var smart = Ui.Button("스마트 전환", Glyphs.Lightning, accent: true, click: async (_, _) => await Flows.SmartSwitchAsync(XamlRoot),
            tip: "Max 20x 계정 중 주간·5시간 한도가 모두 남은 계정에서 주간 초기화가 가장 가까운 계정으로 모든 활성 장치를 전환해요.");
        smart.IsEnabled = !busy && State.Error is null && State.Accounts.Count > 1;
        var refresh = Ui.Button(State.RefreshRequested ? "갱신 중…" : "새로고침", Glyphs.Refresh, click: async (_, _) => await State.RefreshAsync(),
            tip: "사용량 새로고침 (F5)");
        refresh.IsEnabled = !busy;
        refresh.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.F5 });
        var subtitle = keys.Count switch
        {
            0 => "설정에서 표시할 사용량을 켜 주세요.",
            2 => "계정마다 5시간·주간 한도가 얼마나 남았는지 비교해요. 계정을 고르면 전환 화면이 열려요.",
            _ => $"계정마다 {QuotaFormat.Title(keys[0])} 한도가 얼마나 남았는지 비교해요. 계정을 고르면 전환 화면이 열려요.",
        };
        Header("사용량", subtitle, smart, refresh);
        ErrorBar();

        var accounts = State.Accounts;
        if (accounts.Count == 0)
        {
            var add = Ui.Button("계정 추가", Glyphs.Add, accent: true, click: async (_, _) =>
            {
                if (await State.CommandAsync("account.add", success: "브라우저에서 로그인을 마쳐 주세요.") is not null) Shell.Navigate("accounts");
            });
            add.IsEnabled = !busy && State.Error is null;
            Add(Ui.Card(Ui.Stack(16, false, Ui.Text(State.Error is null ? "계정을 추가하면 계정별 사용량을 비교할 수 있어요." : "서비스에 연결하지 못했어요. 새로고침해 주세요.", secondary: true), add)));
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (keys.Contains("weekly") && WeeklySummary(accounts, now) is { } summary) Add(summary);
        var list = new StackPanel { Spacing = 4 };
        if (Wide && keys.Count > 0) list.Children.Add(ColumnHeader(keys));
        var refreshing = State.RefreshRequested || (State.Busy && AppState.Str(State.Operation?["kind"]) == "refresh");
        var updated = new List<DateTimeOffset>();
        foreach (var account in accounts)
        {
            var usage = QuotaFormat.Account(account, keys, now, unavailable: State.ConnectionError is not null, refreshing: refreshing);
            if (usage.Updated is { } time) updated.Add(time);
            list.Children.Add(AccountRow(account, usage, keys, busy));
        }

        Add(list);
        var suffix = $"{accounts.Count}개 계정";
        if (QuotaFormat.Flag(State.Settings?["autoSwitchEnabled"])) suffix += " · 자동 전환 켜짐";
        var footer = updated.Count == 0
            ? $"아직 조회된 사용량이 없어요 · {suffix}"
            : $"마지막 조회 {updated.Min().ToLocalTime():M월 d일 HH:mm} 기준 · {suffix}" + (updated.Count < accounts.Count ? " · 일부 미조회" : "");
        Add(Ui.Caption(footer));
    }

    // Every account's week together, and a countdown to the reset that comes first: the sum grows again then.
    private Border? WeeklySummary(IReadOnlyList<JsonObject> accounts, DateTimeOffset now)
    {
        var pool = WeeklyPool.Of(accounts, now, State.ConnectionError is not null);
        if (pool.Remaining is not { } left) return null;
        var countdown = pool.Countdown(now);
        var timer = countdown is null
            ? Ui.Stack(1, false, Ui.Text("주간 초기화 시각 미정", strong: true), Ui.Caption("초기화 시각을 아는 계정이 없어요."))
            : Ui.Stack(1, false, Ui.Text(countdown, strong: true),
                Ui.Caption($"가장 빨리 초기화되는 계정: {pool.NextResetAccount} · {QuotaFormat.Date(pool.NextReset!.Value)}"));
        var value = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        value.Children.Add(Ui.ToneText(QuotaFormat.Percent(left), pool.Tone, 20));
        var unit = Ui.Caption("남음", wrap: false);
        unit.VerticalAlignment = VerticalAlignment.Bottom;
        unit.Margin = new Thickness(0, 0, 0, 4);
        value.Children.Add(unit);
        var counted = Ui.Caption($"{WeeklyPool.Label} · {pool.Counted}개 계정 합산", wrap: false);
        counted.HorizontalAlignment = HorizontalAlignment.Right;
        var row = Ui.Row(Ui.Columns(16, 1, Ui.Glyph(Glyphs.Clock, 20), timer, Ui.Stack(0, false, value, counted)));
        AutomationProperties.SetName(row, string.Join(", ", new[]
        {
            countdown ?? "주간 초기화 시각 미정",
            countdown is null ? "" : $"{pool.NextResetAccount} {QuotaFormat.Date(pool.NextReset!.Value)}",
            $"{WeeklyPool.Label} {QuotaFormat.Percent(left)} 남음, {pool.Counted}개 계정 합산",
        }.Where(part => part.Length > 0)));
        return row;
    }

    private static Grid Columns()
    {
        var grid = new Grid { ColumnSpacing = 20 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        return grid;
    }

    private static Grid ColumnHeader(IReadOnlyList<string> keys)
    {
        var grid = Columns();
        grid.Padding = new Thickness(17, 0, 17, 4);
        grid.Children.Add(Ui.Caption("계정"));
        for (var i = 0; i < keys.Count; i++)
        {
            var title = Ui.Caption($"{QuotaFormat.Title(keys[i])} 한도");
            Grid.SetColumn(title, i + 1);
            grid.Children.Add(title);
        }

        return grid;
    }

    private UIElement AccountRow(JsonObject account, AccountUsage usage, IReadOnlyList<string> keys, bool busy)
    {
        var id = AppState.Int(account["id"]);
        var name = AppState.AccountName(account);
        var current = id == State.ActiveProfileId;
        var grid = Columns();
        if (!Wide)
        {
            grid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowSpacing = 14;
        }

        var identity = new Grid { ColumnSpacing = 12 };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identity.Children.Add(Ui.Avatar(id, name, 40));
        var names = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Ui.Text(name, strong: true, wrap: false));
        var details = string.Join(" · ", new[] { AppState.Str(account["email"]), AppState.Str(account["plan"]) }.Where(value => !string.IsNullOrEmpty(value) && value != name));
        if (details.Length > 0) names.Children.Add(Ui.Caption(details, wrap: false));
        // One pill per line, so a long status never runs under the quota columns.
        var pills = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        if (current) pills.Children.Add(Ui.Pill(State.ConnectionError is null ? "사용 중" : "마지막 적용 계정", Ui.PillKind.Accent, Glyphs.Check));
        if (usage.Status.Length > 0)
            pills.Children.Add(Ui.Pill(usage.Status, usage.StatusIsWarning ? Ui.PillKind.Caution : Ui.PillKind.Neutral,
                usage.StatusIsWarning ? Glyphs.Lock : Glyphs.Clock));
        if (pills.Children.Count > 0) names.Children.Add(pills);
        Grid.SetColumn(names, 1);
        identity.Children.Add(names);
        if (!Wide) Grid.SetColumnSpan(identity, 3);
        grid.Children.Add(identity);

        for (var i = 0; i < usage.Quotas.Count; i++)
        {
            var cell = QuotaCell(usage.Quotas[i], showTitle: !Wide);
            if (Wide) Grid.SetColumn(cell, i + 1);
            else
            {
                Grid.SetRow(cell, 1);
                Grid.SetColumn(cell, i);
            }

            grid.Children.Add(cell);
        }

        var chevron = Ui.Glyph(Glyphs.ChevronRight, 12, "SyncBarGlyphMuted");
        chevron.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(chevron, 3);
        Grid.SetRowSpan(chevron, 2);
        grid.Children.Add(chevron);

        var button = new Button
        {
            Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(4),
            IsEnabled = !busy && State.Error is null && !AppState.NeedsLogin(account) && id is not null,
        };
        if (current) button.Style = Ui.Style("SyncBarCurrentRowButtonStyle");

        AutomationProperties.SetName(button, string.Join(", ", new[] { name, details }.Concat(usage.Quotas.Select(q => q.Spoken))
            .Append(usage.Status).Append(current ? "사용 중" : "").Where(part => part.Length > 0)));
        ToolTipService.SetToolTip(button, "이 계정의 전환 화면 열기 · 고르기만 해서는 바뀌지 않아요");
        button.Click += (_, _) =>
        {
            if (id is not { } profile) return;
            State.Select(profile);
            Shell.Navigate("switch");
        };
        return button;
    }

    private static StackPanel QuotaCell(Quota quota, bool showTitle)
    {
        var cell = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (showTitle) cell.Children.Add(Ui.Caption(quota.Title));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        line.Children.Add(Ui.ToneText(quota.Value, quota.Tone, 20));
        if (quota.Remaining is not null)
        {
            var unit = Ui.Caption("남음", wrap: false);
            unit.VerticalAlignment = VerticalAlignment.Bottom;
            unit.Margin = new Thickness(0, 0, 0, 4);
            line.Children.Add(unit);
        }

        if (Ui.ToneGlyph(quota.Tone, 13) is { } glyph)
        {
            glyph.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(glyph);
        }

        cell.Children.Add(line);
        cell.Children.Add(Ui.Meter(quota));
        cell.Children.Add(Ui.Caption(quota.Caption));
        return cell;
    }
}
