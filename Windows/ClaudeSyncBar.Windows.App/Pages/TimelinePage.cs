using System.Globalization;
using System.Text.Json.Nodes;
using ClaudeSyncBar.App.Controls;
using ClaudeSyncBar.App.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace ClaudeSyncBar.App.Pages;

/// <summary>
/// One week split between the accounts by their resets and the user's share of each, beside what
/// really happened: which account this PC held, what each reset took and how much was used a day.
/// It only shows; no switch reads the plan. This week's view is the one the quick view keeps, and
/// an earlier week is read when asked for.
/// </summary>
public sealed class TimelinePage : SyncPage
{
    private const string Subtitle = "초기화 시점과 내 몫에 맞춰 일주일을 나누고, 실제로 쓴 양과 견줘 봐요.";
    private const string Footnote = "계정 하나의 주간 한도를 100%로 셌어요. 쓴 양은 몇 분마다 읽은 남은 양으로 어림한 거예요. 타임라인은 보여 주기만 하고 전환 규칙은 바꾸지 않아요.";
    // A band narrower than this has no room for two letters of a name and its usual padding.
    private const double TextRoom = 52;
    // A hold says whose it was down to two letters of the name, cut short, with a little room either side.
    private const double NameRoom = 32;
    private const double TextPadding = 7;
    private const int MinimumWeek = -4;
    // This week's view follows the clock, as the quick view's strip does.
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);
    // The week on screen, 0 for the one that contains now, and its view when it is an earlier one. The week
    // asked for runs ahead of it while a reading is under way, so a second press goes on from the first.
    private int _week;
    private int _target;
    private JsonObject? _earlier;
    private string? _error;
    private bool _loading;
    private int _request;
    // What the last reading was asked for, and when it ended: another follows when the accounts changed or it grew old.
    private string? _readFor;
    private DateTimeOffset _readAt;
    // Each account's name and share as drawn, for the share editors kept from drawing to drawing.
    private readonly Dictionary<int, (string Name, int Share)> _shares = [];
    // A share being saved, shown at once on its button, which waits until the week is read again.
    private readonly Dictionary<int, int> _saving = [];
    // While a share editor is open the page stays as drawn under it: drawn again, the editor's button would leave it and close it.
    private bool _editing;

    public TimelinePage()
    {
        Loaded += (_, _) =>
        {
            State.Changed += Watch;
            _ = LoadAsync(_week);
        };
        Unloaded += (_, _) => State.Changed -= Watch;
    }

    private JsonObject? Shown => _week == 0 ? State.Timeline : _earlier;

    protected override bool Frozen => _editing;

    // A reading under way shows only while there is nothing to show yet.
    protected override string Signature() => string.Join("|", _week, Shown?.ToJsonString(), _error, _loading && Shown is null,
        State.Busy || State.RefreshRequested, string.Join(",", _saving));

    // What the plan and the record are made from: the account in use, the list, and each account's week as last read.
    private static string Key() => State.ActiveProfileId + "|" + string.Join(";", State.Accounts.Select(account => string.Join(",",
        AppState.Int(account["id"]), AppState.AccountName(account), AppState.NeedsLogin(account), QuotaFormat.Flag(account["smartCandidate"]),
        account["usage"]?["weekly"]?["usedPercent"]?.ToJsonString(), account["usage"]?["weekly"]?["resetsAt"]?.ToJsonString())));

    // Every look at the service comes by here; the timeline is read again only when it would differ.
    private void Watch()
    {
        // Behind a closed window nothing is read: the window shown again is told of the next look and reads then.
        if (_loading || _editing || !Shell.AppWindow.IsVisible) return;
        if (Key() != _readFor || (_week == 0 && DateTimeOffset.UtcNow - _readAt > Fresh)) _ = LoadAsync(_week);
    }

    /// <summary>Reads a week. A later answer to an earlier asking is dropped; a failure keeps what is on screen.</summary>
    private async Task LoadAsync(int week)
    {
        var request = ++_request;
        _target = week;
        _loading = true;
        _readFor = Key();
        if (Shown is null) Redraw();
        try
        {
            if (week == 0)
            {
                await State.RefreshTimelineAsync();
                if (request != _request) return;
                _error = State.TimelineError;
                if (_error is null) (_week, _earlier) = (0, null);
            }
            else
            {
                var view = await State.Client.CallAsync("timeline", new { week }, TimeSpan.FromSeconds(60)) as JsonObject;
                if (request != _request) return;
                (_week, _earlier, _error) = (AppState.Int(view?["week"]) ?? week, view, null);
            }
        }
        catch (ServiceError error)
        {
            if (request == _request) _error = error.Message;
        }
        finally
        {
            if (request == _request)
            {
                _loading = false;
                _readAt = DateTimeOffset.UtcNow;
                // A week that could not be read is not the one shown: the next press goes on from the one on screen.
                _target = _week;
                // Under an open share editor the page waits, and is drawn as the editor closes.
                Redraw();
            }
        }
    }

    // After a share changed: the week on screen, and this week's for the quick view when another is shown.
    private async Task ReloadAsync()
    {
        if (_week != 0) _ = State.RefreshTimelineAsync();
        await LoadAsync(_week);
    }

    protected override void Render()
    {
        var view = Shown;
        var weeks = WeekButtons(view);
        if (Wide) Header("주간 타임라인", Subtitle, weeks);
        else
        {
            Header("주간 타임라인", Subtitle);
            weeks.HorizontalAlignment = HorizontalAlignment.Left;
            Add(weeks);
        }

        if (_error is not null) Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = _error });
        if (view is null)
        {
            if (_loading)
                Add(Ui.Card(Ui.Stack(12, true, new ProgressRing { IsActive = true, Width = 20, Height = 20 }, Centered(Ui.Text("일주일 기록을 읽고 있어요…", secondary: true)))));
            return;
        }

        var now = QuotaFormat.Time(view["now"]) ?? DateTimeOffset.UtcNow;
        if (AppState.Str(view["state"]) == "ok")
        {
            if (_week == 0) Add(Summary(view, now));
            Add(Figures(view));
            Add(Chart(view, now));
        }
        else
        {
            Add(Ui.Card(Ui.Stack(4, false, Ui.Text("구간을 나눌 계정이 없어요", strong: true), Ui.Text(TimelineText.EmptyCaption(view), secondary: true))));
        }

        var accounts = TimelineText.Items(view["accounts"]);
        if (accounts.Count > 0)
        {
            Group("계정별 구간", "내 몫은 그 계정 한도에서 내가 쓰는 비율이에요. 바꾸면 구간을 다시 나눠요");
            var list = new StackPanel { Spacing = 4 };
            AutomationProperties.SetName(list, "계정별 구간");
            foreach (var account in accounts) list.Children.Add(Account(account));
            Add(list);
        }

        Add(Ui.Caption(Footnote));
    }

    // ---------- week ----------

    private StackPanel WeekButtons(JsonObject? view)
    {
        var earlier = Keep("earlier", () => Ui.IconButton(MoreGlyphs.ChevronLeft, "지난 주", (_, _) => _ = LoadAsync(Math.Max(MinimumWeek, _target - 1)), subtle: false));
        var later = Keep("later", () => Ui.IconButton(Glyphs.ChevronRight, "다음 주", (_, _) => _ = LoadAsync(Math.Min(0, _target + 1)), subtle: false));
        earlier.IsEnabled = view is not null && QuotaFormat.Flag(view["hasEarlier"]);
        later.IsEnabled = _week < 0;
        var range = Centered(Ui.Text(TimelineText.Range(view) is { Length: > 0 } days ? days : _week == 0 ? "이번 주" : "", strong: true, wrap: false));
        range.Margin = new Thickness(8, 0, 8, 0);
        AutomationProperties.SetName(range, "보고 있는 주: " + range.Text);
        return Ui.Stack(4, true, earlier, range, later);
    }

    // ---------- whose turn ----------

    private Border Summary(JsonObject view, DateTimeOffset now)
    {
        var title = TimelineText.TurnTitle(view);
        var caption = TimelineText.TurnCaption(view, now);
        var text = Ui.Stack(1, false, Ui.Text(title, strong: true));
        if (caption.Length > 0) text.Children.Add(Ui.Caption(caption));
        var spoken = new List<string> { title, caption };
        StackPanel? after = null;
        if (view["summary"]?["turn"] is not null && view["summary"]?["next"] is JsonObject next)
        {
            var name = $"다음은 {AppState.Str(next["name"]) ?? "계정"}";
            var from = QuotaFormat.Time(next["startsAt"]) is { } starts ? $"{QuotaFormat.Clock(starts, now)}부터" : null;
            after = Ui.Stack(1, false, Ui.Text(name, strong: true, wrap: false));
            if (from is not null) after.Children.Add(Ui.Caption(from, wrap: false));
            if (Wide) foreach (var line in after.Children.OfType<TextBlock>()) line.HorizontalAlignment = HorizontalAlignment.Right;
            spoken.Add(name);
            spoken.Add(from ?? "");
        }

        var row = Wide || after is null
            ? Ui.Row(Ui.Columns(16, 1, Ui.Glyph(Glyphs.Clock, 20), text, after))
            : Ui.Row(Ui.Columns(16, 1, Ui.Glyph(Glyphs.Clock, 20), Ui.Stack(8, false, text, after)));
        AutomationProperties.SetName(row, string.Join(", ", spoken.Where(part => part.Length > 0)));
        return row;
    }

    // ---------- three figures ----------

    private Border Figures(JsonObject view)
    {
        var summary = view["summary"];
        var total = TimelineText.Percent(QuotaFormat.Number(view["totalShare"]) ?? 0);
        var cells = new List<StackPanel>
        {
            Figure(_week == 0 ? "이번 주 쓴 양" : "그 주에 쓴 양", TimelineText.Percent(QuotaFormat.Number(summary?["used"]) ?? 0),
                _week == 0 ? $"지금까지 계획 {TimelineText.Percent(QuotaFormat.Number(summary?["plannedSoFar"]) ?? 0)}" : $"계획 {total}"),
            Figure("사라진 양", TimelineText.Lost(view), TimelineText.LostCaption(view)),
        };
        // An account shared with others, the first in the plan: how much of it the user took since its reset. That is
        // the window running now, so an earlier week shows the day's plan instead.
        var shared = _week < 0 ? null
            : TimelineText.Items(view["accounts"]).FirstOrDefault(row => QuotaFormat.Flag(row["planned"]) && AppState.Int(row["share"]) is < 100);
        if (shared is not null)
        {
            var since = QuotaFormat.Time(shared["resetsAt"]) is { } resets ? $"{TimelineText.DayTime(resets.AddDays(-7))} 초기화 뒤 · " : "";
            cells.Add(Figure($"{AppState.Str(shared["name"]) ?? "계정"}에서 쓴 양", TimelineText.Percent(QuotaFormat.Number(shared["mineUsed"])),
                $"{since}내 몫 {AppState.Int(shared["share"])}% 중"));
        }
        else cells.Add(Figure("하루 계획", TimelineText.Percent(QuotaFormat.Number(view["dailyShare"]) ?? 0), $"내 몫 합계 {total}"));

        // A row of the page like the turn above it, not a card with a subject of its own.
        static Border Box(UIElement figures)
        {
            var box = Ui.Row(figures);
            box.Padding = new Thickness(16);
            return box;
        }

        if (!Wide) return Box(Ui.Stack(16, false, [.. cells]));
        // The middle figure names an account and a date under it, so it gets the most room: its line stays whole at the usual width.
        double[] shares = [0.8, 1.6, 1.2];
        var grid = new Grid { ColumnSpacing = 24 };
        for (var i = 0; i < cells.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(shares[i], GridUnitType.Star) });
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }

        return Box(grid);
    }

    private static StackPanel Figure(string title, string value, string caption)
    {
        var cell = Ui.Stack(2, false, Ui.Caption(title, wrap: false), Ui.Text(value, 20, strong: true, wrap: false), Ui.Caption(caption));
        AutomationProperties.SetName(cell, $"{title} {value}, {caption}");
        return cell;
    }

    // ---------- plan and record ----------

    private Border Chart(JsonObject view, DateTimeOffset now)
    {
        var days = TimelineText.Items(view["days"]);
        var daily = QuotaFormat.Number(view["dailyShare"]) ?? 0;
        var nowAt = QuotaFormat.Number(view["nowAt"]);
        var clock = QuotaFormat.Time(view["startsAt"])?.ToLocalTime().ToString("HH:mm") ?? "00:00";
        var note = $"하루는 {clock}부터 다음 날 {clock}까지로 셌어요 · 점선은 하루 계획 {TimelineText.Percent(daily)}";
        FrameworkElement head = Wide
            ? Ui.Columns(8, 0, Ui.Text("계획과 실제", strong: true), Ui.Caption(note, wrap: false))
            : Ui.Stack(2, false, Ui.Text("계획과 실제", strong: true), Ui.Caption(note));

        // One stop of the Tab key; the arrow keys go from part to part inside, each with its tooltip.
        var grid = new Grid
        {
            ColumnSpacing = Wide ? 12 : 8, RowSpacing = 8, TabFocusNavigation = KeyboardNavigationMode.Once,
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Wide ? 64 : 60) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var height in nowAt is null ? new double[] { 24, 44, 36, 44, 88 } : [24, 44, 36, 44, 88, 16])
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });

        // The lines between days run behind every row down to the bars.
        var lines = new TimelinePanel();
        for (var i = 1; i < days.Count; i++) lines.Mark(new Border { Width = 1, Style = Ui.Style("SyncBarTimelineGrid") }, (double)i / days.Count);
        grid.Children.Add(On(lines, 0, 1, 5));

        grid.Children.Add(On(DayHeader(days, now), 0));
        grid.Children.Add(On(RowLabel(VerticalAlignment.Center, "계획 구간"), 1, 0));
        // Where the now line comes down over the rows, their words step aside for it.
        var clear = nowAt ?? double.NaN;
        grid.Children.Add(On(Named(Plan(view, clear), "계획 구간"), 1));
        grid.Children.Add(On(RowLabel(VerticalAlignment.Center, "초기화"), 2, 0));
        grid.Children.Add(On(Named(Resets(view, now, clear), "초기화"), 2));
        grid.Children.Add(On(RowLabel(VerticalAlignment.Center, "실제", "남은 양"), 3, 0));
        grid.Children.Add(On(Named(Holds(view), "실제로 쓴 계정과 남은 양"), 3));
        grid.Children.Add(On(RowLabel(VerticalAlignment.Bottom, "하루 쓴 양"), 4, 0));
        grid.Children.Add(On(Named(Bars(days, daily, clear), "하루 쓴 양"), 4));

        if (nowAt is { } at)
        {
            var time = $"지금 {now.ToLocalTime():HH:mm}";
            var stamp = new TimelinePanel();
            stamp.Label(Ui.Text(time, 12, strong: true, wrap: false), at, before: at > 0.8, offset: 6);
            grid.Children.Add(On(stamp, 5));
            // From the plan down through the bars, over everything.
            var line = new TimelinePanel();
            AutomationProperties.SetName(line.Mark(new Border { Width = 2, CornerRadius = new CornerRadius(1), Style = Ui.Style("SyncBarTimelineNow") }, at), time);
            grid.Children.Add(On(line, 1, 1, 5));
        }

        var body = Ui.Stack(12, false, head, grid);
        if (QuotaFormat.Time(view["recordStartsAt"]) is not { } record)
            body.Children.Add(Ui.Caption("기록이 아직 없어요. 쓴 양은 몇 분마다 남은 양을 읽으면서 쌓여요."));
        else if (QuotaFormat.Time(view["startsAt"]) is { } starts && record > starts)
            body.Children.Add(Ui.Caption($"기록은 {QuotaFormat.Date(record)}부터 있어요."));
        var card = Ui.Card(body);
        AutomationProperties.SetName(card, "계획과 실제");
        return card;
    }

    private static T On<T>(T element, int row, int column = 1, int rows = 1) where T : FrameworkElement
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetRowSpan(element, rows);
        return element;
    }

    private static T Named<T>(T element, string name) where T : FrameworkElement
    {
        AutomationProperties.SetName(element, name);
        return element;
    }

    private static T Centered<T>(T element) where T : FrameworkElement
    {
        element.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }


    private static StackPanel RowLabel(VerticalAlignment align, params string[] lines)
    {
        var label = Ui.Stack(0, false, [.. lines.Select(line => Ui.Caption(line, wrap: false))]);
        label.VerticalAlignment = align;
        return label;
    }

    private TimelinePanel DayHeader(IReadOnlyList<JsonObject> days, DateTimeOffset now)
    {
        var header = new TimelinePanel();
        for (var i = 0; i < days.Count; i++)
        {
            var today = TimelineText.IsToday(days[i], now);
            var label = Centered(Ui.Text(TimelineText.DayLabel(days[i]), 12, strong: today, secondary: !today, wrap: false));
            label.Margin = new Thickness(Wide ? 8 : 4, 0, 0, 0);
            if (today) AutomationProperties.SetName(label, $"{label.Text}, 오늘");
            header.Band(label, (double)i / days.Count, 1.0 / days.Count);
        }

        return header;
    }

    // Two lines in a band: a name in bold over what the row says of it.
    private static StackPanel BandText(string name, string? detail)
    {
        var text = Centered(Ui.Stack(0, false, Ui.Text(name, 13, strong: true, wrap: false)));
        // A time or an amount cut short would say another: it shows whole or not at all, and the name is cut instead.
        if (detail is not null) text.Children.Add(TimelinePanel.Whole(Ui.Caption(detail, wrap: false)));
        return text;
    }

    private static TimelinePanel Plan(JsonObject view, double clear)
    {
        var plan = new TimelinePanel { Gap = 2, Avoid = clear };
        foreach (var segment in TimelineText.Items(view["segments"]))
        {
            var name = AppState.Str(segment["name"]) ?? "계정";
            if (QuotaFormat.Time(segment["startsAt"]) is not { } starts || QuotaFormat.Time(segment["endsAt"]) is not { } ends) continue;
            var id = AppState.Int(segment["profileId"]);
            var band = new TimelineSpot(new Border { Style = Ui.Style("SyncBarTimelinePlan") },
                $"{name} 계획 구간 · {QuotaFormat.Date(TimelineText.Edge(view, id, starts))} ~ {QuotaFormat.Date(TimelineText.Edge(view, id, ends))} · 내 몫 {AppState.Int(segment["share"]) ?? 100}%");
            plan.Band(band, QuotaFormat.Number(segment["at"]) ?? 0, QuotaFormat.Number(segment["width"]) ?? 0);
            plan.Text(BandText(name, $"{TimelineText.EdgeText(view, id, ends)}까지"), band, TextPadding, TextRoom);
        }

        return plan;
    }

    // A tick at each reset and two lines beside it: whose it is, and what it took or when it comes.
    private static TimelinePanel Resets(JsonObject view, DateTimeOffset now, double clear)
    {
        var resets = new TimelinePanel { Avoid = clear };
        foreach (var reset in TimelineText.Items(view["resets"]))
        {
            var name = AppState.Str(reset["name"]) ?? "계정";
            var at = QuotaFormat.Number(reset["at"]) ?? 0;
            var status = TimelineText.ResetStatus(reset, now);
            var tick = new Border { Width = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 2, 0, 2), Style = Ui.Style("SyncBarTimelineTick") };
            var when = QuotaFormat.Time(reset["resetsAt"]) is { } time ? $" · {QuotaFormat.Date(time)}" : "";
            resets.Mark(new TimelineSpot(tick, $"{name} 초기화{when}" + (status.Length > 0 ? $" · {status}" : "")), at);

            // In the last fifth of the week the words go to the left of the tick, else to its right; never to the other
            // side, where they would read as the neighbouring tick's. Without room they are left to the tooltip.
            var label = Ui.Stack(0, false, Ui.Caption(name, wrap: false));
            if (status.Length > 0) label.Children.Add(TimelinePanel.Whole(Ui.Text(status, 12, wrap: false)));
            label.Margin = new Thickness(0, 2, 0, 0);
            resets.Label(label, at, before: at > 0.8, offset: 7, minText: 28, collide: true);
        }

        return resets;
    }

    // The account this PC held, with what was left of it at each end. The one in use now takes the current-account colours.
    private static TimelinePanel Holds(JsonObject view)
    {
        var holds = new TimelinePanel { Gap = 2 };
        var current = AppState.Int(TimelineText.Items(view["accounts"]).FirstOrDefault(row => QuotaFormat.Flag(row["isCurrent"]))?["profileId"]);
        foreach (var hold in TimelineText.Items(view["holds"]))
        {
            var name = AppState.Str(hold["name"]) ?? "계정";
            if (QuotaFormat.Time(hold["startsAt"]) is not { } starts || QuotaFormat.Time(hold["endsAt"]) is not { } ends) continue;
            var at = QuotaFormat.Number(hold["at"]) ?? 0;
            var width = QuotaFormat.Number(hold["width"]) ?? 0;
            var open = QuotaFormat.Flag(hold["isOpen"]);
            var inUse = open && current is not null && AppState.Int(hold["profileId"]) == current;
            var left = QuotaFormat.Number(hold["remainingFrom"]) is { } from && QuotaFormat.Number(hold["remainingTo"]) is { } to
                ? TimelineText.LeftRange(from, to) : null;
            var band = new TimelineSpot(new Border { Style = Ui.Style(inUse ? "SyncBarTimelineHoldCurrent" : "SyncBarTimelineHold") },
                (open ? $"{name} · {QuotaFormat.Date(starts)}부터 사용 중" : $"{name} · {QuotaFormat.Date(starts)} ~ {QuotaFormat.Date(ends)}")
                + (left is null ? "" : $" · 남은 양 {left}"));
            holds.Band(band, at, width);
            // A short hold still says whose it was, cut short; the one in use is named beside its band instead.
            var words = holds.Text(BandText(name, left), band, TextPadding, inUse ? TextRoom : NameRoom);
            if (!inUse) continue;
            // Held only a short while, the account in use is named to the right of its band, where nothing is yet.
            var note = Ui.Caption("사용 중", wrap: false);
            note.Style = Ui.Style("SyncBarAccentText");
            note.FontSize = 12;
            holds.Label(Centered(Ui.Stack(0, false, Ui.Text(name, 13, strong: true, wrap: false), note)), at + width, before: false, offset: 8, minText: 36, instead: words);
        }

        return holds;
    }

    // One bar a day that has begun, against the dashed line of the day's plan. The bars are amounts used: neutral.
    private Grid Bars(IReadOnlyList<JsonObject> days, double daily, double clear)
    {
        const double area = 68;
        var bars = new TimelinePanel();
        // Each day's amount under its bar. Today's steps to the right of the now line when that line would run through it.
        var values = new TimelinePanel { VerticalAlignment = VerticalAlignment.Bottom, Avoid = clear };
        // The larger of a whole account's week and the biggest day fits; the plan's line too, when many accounts make it higher.
        var scale = days.Select(day => Math.Max(QuotaFormat.Number(day["used"]) ?? 0, QuotaFormat.Number(day["planned"]) ?? daily)).Append(100).Max();
        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            var planned = Math.Clamp(QuotaFormat.Number(day["planned"]) ?? daily, 0, scale);
            // The whole column answers the pointer, over the amount under it too.
            var column = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            var plot = new Grid { Height = area, VerticalAlignment = VerticalAlignment.Top };
            var spoken = $"{TimelineText.DayDate(day)} · 아직 오지 않은 날 · 하루 계획 {TimelineText.Percent(planned)}";
            if (QuotaFormat.Number(day["used"]) is { } used)
            {
                used = Math.Clamp(used, 0, scale);
                plot.Children.Add(Level(scale, used, new Border
                {
                    Style = Ui.Style("SyncBarTimelineBar"), Width = Wide ? 24 : 20, HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(Wide ? 8 : 4, 0, 0, 0), CornerRadius = new CornerRadius(2, 2, 0, 0), MinHeight = used > 0 ? 1 : 0,
                    VerticalAlignment = VerticalAlignment.Bottom,
                }, fill: true));
                values.Label(TimelinePanel.Whole(Ui.Text(TimelineText.Percent(used), 12, strong: true, wrap: false)), (double)i / days.Count, before: false, offset: Wide ? 8 : 4, dodge: true);
                spoken = $"{TimelineText.DayDate(day)} · {TimelineText.Percent(used)} 씀 · 하루 계획 {TimelineText.Percent(planned)}";
            }

            plot.Children.Add(Level(scale, planned, new Line
            {
                X1 = 0, X2 = 1, Stretch = Stretch.Fill, StrokeDashArray = [3, 3], Style = Ui.Style("SyncBarTimelinePlanLine"), VerticalAlignment = VerticalAlignment.Top,
            }, fill: false));
            plot.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Style = Ui.Style("SyncBarTimelineGrid") });
            column.Children.Add(plot);
            bars.Band(new TimelineSpot(column, spoken), (double)i / days.Count, 1.0 / days.Count);
        }

        var row = new Grid();
        row.Children.Add(values);
        row.Children.Add(bars);
        return row;
    }

    // An element at a height of the bar area: the part of the scale above it and the part it stands on, as two star rows.
    private static Grid Level(double scale, double value, FrameworkElement element, bool fill)
    {
        var level = new Grid();
        level.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(0, scale - value), GridUnitType.Star) });
        level.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(0, value), GridUnitType.Star) });
        if (fill) element.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(element, 1);
        level.Children.Add(element);
        return level;
    }

    // ---------- accounts ----------

    private Border Account(JsonObject row)
    {
        var id = AppState.Int(row["profileId"]) ?? 0;
        var name = AppState.Str(row["name"]) ?? "계정";
        var share = AppState.Int(row["share"]) ?? 100;
        var current = QuotaFormat.Flag(row["isCurrent"]);
        var spoken = new List<string> { name };

        var names = Ui.Wrap(8, Ui.Text(name, strong: true, wrap: false),
            QuotaFormat.Flag(row["smartCandidate"]) ? null : Ui.Pill("스마트 전환 제외", Ui.PillKind.Neutral),
            current ? Ui.Pill("사용 중", Ui.PillKind.Accent, Glyphs.Check) : null);
        names.LineSpacing = 4;
        var reset = QuotaFormat.Time(row["resetsAt"]) is { } resets
            ? $"{QuotaFormat.Date(resets)} 초기화" + (QuotaFormat.Flag(row["resetProjected"]) ? " 예상" : "") : "초기화 시각 모름";
        var identity = Ui.Columns(12, 1, Ui.Avatar(id, name, 40), Ui.Stack(1, false, names, Ui.Caption(reset, wrap: false)));
        if (current) spoken.Add("사용 중");
        if (!QuotaFormat.Flag(row["smartCandidate"])) spoken.Add("스마트 전환 제외");
        spoken.Add(reset);

        _shares[id] = (name, share);
        var change = Keep($"share:{id}", () => ShareButton(id));
        // A share being saved shows at once; its button waits for the week to be split again.
        var asked = _saving.TryGetValue(id, out var saving) ? saving : share;
        change.Content = $"{asked}%";
        change.IsEnabled = !State.Busy && !State.RefreshRequested && !_saving.ContainsKey(id);
        AutomationProperties.SetName(change, $"{name} 내 몫 {asked}%, 바꾸기");
        var mine = Ui.Stack(8, true, Centered(Ui.Caption("내 몫", wrap: false)), change);
        spoken.Add($"내 몫 {asked}%");

        FrameworkElement span;
        if (QuotaFormat.Flag(row["planned"]) && QuotaFormat.Time(row["segmentStartsAt"]) is { } starts && QuotaFormat.Time(row["segmentEndsAt"]) is { } ends)
        {
            var range = $"{TimelineText.EdgeText(Shown, id, starts)} ~ {TimelineText.EdgeText(Shown, id, ends)}";
            var slack = QuotaFormat.Number(row["slackHours"]) is >= 1 and var hours ? $"초기화까지 여유 {TimelineText.Whole(hours)}시간" : "여유 없음 · 초기화와 맞닿아요";
            span = Ui.Stack(1, false, Ui.Text(range, wrap: false), Ui.Caption(slack));
            spoken.Add($"{range}, {slack}");
        }
        else
        {
            var reason = AppState.Str(row["excluded"]) switch
            {
                "login" => "재로그인이 필요해 계획에서 뺐어요",
                "share" => "내 몫이 0%라 계획에서 뺐어요",
                _ => "초기화 시각을 몰라 계획에서 뺐어요",
            };
            span = Ui.Caption(reason);
            spoken.Add(reason);
        }

        var left = QuotaFormat.Number(row["mineLeft"]);
        // A value read some time ago, or before a failed reading, is shown as the usage page shows it: muted, with why.
        var stale = left is null ? null
            : TimelineText.Stale(State.Accounts.FirstOrDefault(account => AppState.Int(account["id"]) == id), DateTimeOffset.UtcNow, State.ConnectionError is not null);
        // The colour says how much of the user's own share is left, as the meters say it of a whole account.
        var tone = left is { } known && share > 0 && stale is null ? QuotaFormat.ToneOf(known / share * 100) : Tone.Muted;
        var amount = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        amount.Children.Add(Ui.ToneText(TimelineText.Left(left), tone, 20));
        if (left is not null)
        {
            // What is left is now's, also beside an earlier week.
            var unit = Ui.Caption(_week < 0 ? "지금 남음" : "남음", wrap: false);
            unit.VerticalAlignment = VerticalAlignment.Bottom;
            unit.Margin = new Thickness(0, 0, 0, 4);
            amount.Children.Add(unit);
            if (Ui.ToneGlyph(tone, 13) is { } glyph) amount.Children.Add(Centered(glyph));
            spoken.Add($"{TimelineText.Left(left)} {unit.Text}");
        }

        var remaining = Ui.Stack(0, false, amount);
        var notes = new List<string>();
        if (left is not null && share < 100) notes.AddRange([$"내 몫 {share}% 중", $"계정 전체는 {TimelineText.Left(QuotaFormat.Number(row["remaining"]))}"]);
        // Why a value is an earlier one, and that it is, a line each in the narrow column.
        if (stale is not null) notes.AddRange(stale.Split(" · "));
        foreach (var line in notes)
        {
            var caption = Ui.Caption(line, wrap: false);
            caption.HorizontalAlignment = HorizontalAlignment.Right;
            remaining.Children.Add(caption);
            spoken.Add(line);
        }

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 12 };
        FrameworkElement[] cells = [identity, mine, span, remaining];
        foreach (var cell in cells) cell.VerticalAlignment = VerticalAlignment.Center;
        if (Wide)
        {
            // The share and the amount left keep their widths; the account and its segment take the rest, the account a little more.
            grid.ColumnSpacing = 12;
            foreach (var width in new[] { new GridLength(1.2, GridUnitType.Star), new GridLength(96), new GridLength(1, GridUnitType.Star), new GridLength(96) })
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            for (var i = 0; i < cells.Length; i++) grid.Children.Add(On(cells[i], 0, i));
        }
        else
        {
            // Narrow: the account and what is left of it on one line, its share and segment under them.
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumnSpan(identity, 2);
            grid.Children.Add(On(identity, 0, 0));
            grid.Children.Add(On(remaining, 0, 2));
            grid.Children.Add(On(mine, 1, 0));
            Grid.SetColumnSpan(span, 2);
            grid.Children.Add(On(span, 1, 1));
        }

        var card = Ui.Row(grid, current);
        AutomationProperties.SetName(card, string.Join(", ", spoken));
        return card;
    }

    // The share's button and its editor: a whole percent from 0 to 100 in steps of 5, saved at once and the week split again.
    private Button ShareButton(int id)
    {
        var button = new Button { MinWidth = 60 };
        ToolTipService.SetToolTip(button, "내 몫 바꾸기");
        var title = Ui.Text("", strong: true);
        var box = new NumberBox
        {
            Minimum = 0, Maximum = 100, SmallChange = 5, LargeChange = 10, Width = 148,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        };
        var save = Ui.Button("저장", accent: true);
        var cancel = Ui.Button("취소");
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.Bottom,
            Content = Ui.Stack(12, false, Ui.Stack(2, false, title, Ui.Caption("이 계정 주간 한도에서 내가 쓰는 비율이에요.")),
                Ui.Stack(8, true, box, Centered(Ui.Text("%"))), Ui.Stack(8, true, save, cancel)),
        };
        flyout.Opening += (_, _) =>
        {
            var (name, share) = _shares.GetValueOrDefault(id, ("계정", 100));
            title.Text = $"{name} 내 몫";
            box.Value = share;
            AutomationProperties.SetName(box, $"{name} 내 몫, 퍼센트");
            _editing = true;
        };
        flyout.Closed += (_, _) =>
        {
            _editing = false;
            if (!IsLoaded) return;
            // What arrived or changed under the editor is drawn now.
            Refresh();
            Watch();
        };

        // The box makes what was typed its value only as the key comes up or the focus leaves it, after Enter
        // went down: the text itself is read, so Enter saves what is on screen.
        int? Typed()
        {
            var value = double.TryParse(Inside<TextBox>(box)?.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var typed) ? typed : box.Value;
            return double.IsNaN(value) ? null : (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 100);
        }

        // An emptied box saves nothing, as cancel does. A share saved shows on its button at once, which waits until
        // the week is split again, so the editor never opens on the old share in between.
        async Task SaveAsync()
        {
            var typed = Typed();
            flyout.Hide();
            if (typed is not { } percent || _saving.ContainsKey(id) || (_shares.TryGetValue(id, out var drawn) && drawn.Share == percent)) return;
            _saving[id] = percent;
            Redraw();
            try
            {
                if (await State.CommandAsync("account.share", new { profileId = id, percent }) is not null) await ReloadAsync();
            }
            finally
            {
                _saving.Remove(id);
                Redraw();
            }
        }

        save.Click += async (_, _) => await SaveAsync();
        cancel.Click += (_, _) => flyout.Hide();
        // Enter saves, but not the Enter still held down from opening the editor.
        box.AddHandler(KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key == global::Windows.System.VirtualKey.Enter && !args.KeyStatus.WasKeyDown) _ = SaveAsync();
        }), true);
        button.Flyout = flyout;
        return button;
    }

    private static T? Inside<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (Inside<T>(child) is { } deeper) return deeper;
        }

        return null;
    }
}
