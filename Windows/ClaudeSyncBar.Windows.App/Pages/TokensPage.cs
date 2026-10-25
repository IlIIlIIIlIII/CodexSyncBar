using System.Globalization;
using System.Text.Json.Nodes;
using ClaudeSyncBar.App.Controls;
using ClaudeSyncBar.App.Model;
using ClaudeSyncBar.Windows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClaudeSyncBar.App.Pages;

/// <summary>
/// Tokens in Claude Code's session logs on each device over a chosen period: how many an hour, what the
/// prompts and outputs are made of, how far a million of them moved each account's five-hour and weekly
/// use, and the totals priced at API rates as an estimate. The logs are read when the page opens and on
/// request; another period or basis is cut from that reading.
/// </summary>
public sealed class TokensPage : SyncPage
{
    private static readonly (int Hours, string Label)[] Presets = [(1, "1시간"), (5, "5시간"), (24, "24시간"), (168, "7일"), (720, "30일")];
    private static readonly (string Id, string Label, string Short, string Description)[] Bases =
    [
        (TokenBurnBasis.Uncached, "캐시 읽기 제외", "캐시 읽기 제외 토큰", "캐시 쓰기·캐시 없는 입력·출력을 1개씩 세요. 입력의 96%인 캐시 읽기를 빼서 대화 길이에 덜 흔들려요."),
        (TokenBurnBasis.Total, "전체 토큰", "전체 토큰", "아래 합계의 전체 토큰과 같은 기준이에요. 대부분이 캐시 읽기라 값이 아주 작아요."),
        (TokenBurnBasis.Weighted, "요금 환산", "요금 환산 토큰", "API 요금 비율로 세요. Opus 5.5 입력 1개를 1로, 캐시 읽기는 0.05개, 출력은 5개로 쳐요."),
    ];
    private const string RoughTip = "한도가 1%p 단위로만 보여서 변화가 작으면 오차가 커요. 기간을 늘리면 정확해져요.";
    private static readonly HashSet<string> Open = ["local"];
    private static readonly HashSet<int> OpenAccounts = [];
    private static bool _openedCurrent;
    // The last reading, shown at once when the page opens again while a new one is made.
    private static JsonObject? _last;
    private static string? _warningClosedFor;
    // What the user chose last, ahead of the answer that shows it; the custom hours come back when 직접 is chosen again.
    private static int? _chosenHours;
    private static string? _chosenBasis;
    private static int _customHours = 36;
    private JsonObject? _result = _last;
    private string? _error;
    private bool _loading;
    private int _loadRequest;
    private int _viewRequest;
    private bool _custom;
    private bool _syncing;

    public TokensPage()
    {
        Loaded += (_, _) => { if (!_loading) _ = LoadAsync(); };
    }

    private JsonObject? View => _result?["view"] as JsonObject;
    private static int Hours(JsonObject view) => AppState.Int(view["hours"]) ?? TokenRateCalculator.MaximumHours;
    private int ChosenHours => _chosenHours ?? (View is { } view ? Hours(view) : TokenRateCalculator.MaximumHours);
    private string Basis => _chosenBasis ?? AppState.Str(View?["basis"]) ?? TokenBurnBasis.Uncached;

    protected override string Signature() => string.Join("|", _result?.ToJsonString(), _error, _loading, _custom, State.Error,
        State.Snapshot["settings"]?.ToJsonString());

    /// <summary>Reads every device's logs again, for the period and basis chosen.</summary>
    private async Task LoadAsync()
    {
        _loading = true;
        var load = ++_loadRequest;
        Redraw();
        var asked = (Hours: _chosenHours, Basis: _chosenBasis);
        try
        {
            object? arguments = asked.Hours is { } hours ? new { hours, basis = Basis } : null;
            var result = await State.Client.CallAsync("tokens", arguments, timeout: TimeSpan.FromSeconds(120)) as JsonObject;
            if (load != _loadRequest) return;
            _result = _last = result;
            _error = null;
            // A view asked for before this reading is older than it.
            ++_viewRequest;
            if (_chosenHours != asked.Hours || _chosenBasis != asked.Basis) _ = ViewAsync();
        }
        catch (ServiceError error)
        {
            if (load == _loadRequest) _error = error.Message;
        }
        finally
        {
            if (load == _loadRequest) _loading = false;
            Redraw();
        }
    }

    /// <summary>The chosen period and basis cut from the last reading; the backend remembers them for next time.</summary>
    private async Task ViewAsync()
    {
        var request = ++_viewRequest;
        try
        {
            var result = await State.Client.CallAsync("tokens.view", new { hours = ChosenHours, basis = Basis }, timeout: TimeSpan.FromSeconds(120)) as JsonObject;
            if (request != _viewRequest || result?["view"] is not JsonObject view) return;
            _result ??= new JsonObject();
            _result["view"] = view.DeepClone();
            _last = _result;
            _error = null;
        }
        catch (ServiceError error)
        {
            if (request == _viewRequest) _error = error.Message;
        }
        finally
        {
            if (request == _viewRequest) Redraw();
        }
    }

    protected override void Render()
    {
        var again = Keep("again", () => Ui.Button("다시 집계", Glyphs.Refresh, click: async (_, _) => await LoadAsync()));
        Ui.Relabel(again, _loading ? "집계 중…" : "다시 집계");
        again.IsEnabled = !_loading;
        Header("토큰 집계", "Claude Code 세션 기록을 고른 기간으로 집계해요. 비용은 API 요금으로 환산한 추정치라 실제 청구 금액과 달라요.", again);
        if (_error is not null) Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = _error });
        var view = View;
        var failed = view is null ? [] : FailedDevices(view);
        if (failed.Count > 0) Add(DeviceWarning(failed));
        Add(Period(view));
        if (view is null)
        {
            if (_loading)
                Add(Ui.Card(Ui.Stack(12, true, new ProgressRing { IsActive = true, Width = 20, Height = 20 },
                    Ui.Stack(2, false, Ui.Text("세션 기록을 집계하고 있어요…", secondary: true), Ui.Caption("처음에는 1분쯤 걸릴 수 있어요.")))));
            return;
        }

        Add(Throughput(view, failed));
        if (Burn(view) is { } burn) Add(burn);
        // A device not read stays named here too, where the warning above can be closed.
        Group("기간 합계", failed.Count == 0 ? "고른 기간 동안 쓴 양이에요." : $"고른 기간 동안 쓴 양이에요. {string.Join(", ", failed)} 기록은 빠졌어요.");
        Add(Totals(view));
        var list = new StackPanel { Spacing = 4 };
        AutomationProperties.SetName(list, "장치별 토큰");
        var devices = (view["devices"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        foreach (var device in devices) list.Children.Add(Device(device));
        if (devices.Count == 0) list.Children.Add(Ui.Card(Ui.Text("집계된 기록이 없어요.", secondary: true)));
        Add(list);
        if (QuotaFormat.Time(_result?["collectedAt"] ?? view["endedAt"]) is { } collected)
            Add(Ui.Caption($"{collected.ToLocalTime():M월 d일 HH:mm} 집계 · 요금을 모르는 모델은 비용에서 빠져요."));
    }

    // ---------- period ----------

    private FrameworkElement Period(JsonObject? view)
    {
        var hours = ChosenHours;
        // A period that is no preset, as one remembered from last time, is a custom one.
        if (!_custom && !Presets.Any(preset => preset.Hours == hours)) { _custom = true; _customHours = hours; }
        var bar = Keep("period", () =>
        {
            var made = new SelectorBar();
            foreach (var (preset, label) in Presets) made.Items.Add(new SelectorBarItem { Text = label, Tag = preset });
            made.Items.Add(new SelectorBarItem { Text = "직접", Tag = 0 });
            AutomationProperties.SetName(made, "기간");
            // The template slides its items in from wherever they were last laid out, and the bar is placed
            // again on every drawing: each choice sent the periods in from the lower right.
            made.Loaded += (_, _) =>
            {
                if (VisualTreeHelper.GetChildrenCount(made) > 0 && VisualTreeHelper.GetChild(made, 0) is Panel root) root.ChildrenTransitions?.Clear();
            };
            made.SelectionChanged += (_, _) =>
            {
                if (_syncing || made.SelectedItem?.Tag is not int chosen) return;
                _custom = chosen == 0;
                _chosenHours = _custom ? _customHours : chosen;
                Redraw();
                _ = ViewAsync();
            };
            return made;
        });
        var box = Keep("hours", () =>
        {
            var made = new NumberBox
            {
                Minimum = 1, Maximum = TokenRateCalculator.MaximumHours, SmallChange = 1, LargeChange = 24, Width = 136,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            };
            AutomationProperties.SetName(made, "기간(시간)");
            made.ValueChanged += (sender, changed) =>
            {
                if (_syncing || double.IsNaN(changed.NewValue)) return;
                var chosen = (int)Math.Clamp(Math.Round(changed.NewValue), 1, TokenRateCalculator.MaximumHours);
                if (chosen == ChosenHours) return;
                _customHours = chosen;
                _chosenHours = chosen;
                _ = ViewAsync();
            };
            return made;
        });

        _syncing = true;
        bar.SelectedItem = bar.Items.FirstOrDefault(item => item.Tag is int tag && (_custom ? tag == 0 : tag == hours)) ?? bar.Items[^1];
        if (box.Value != hours) box.Value = hours;
        _syncing = false;
        // Switched on while out of the page, the periods kept the grey they took while the logs were read
        // until each was chosen once; they are switched once placed.
        var enabled = view is not null;
        DispatcherQueue.TryEnqueue(() => { bar.IsEnabled = enabled; box.IsEnabled = enabled; });

        var custom = _custom ? Ui.Stack(8, true, Centered(Ui.Text("최근")), box, Centered(Ui.Text("시간")), Centered(Ui.Caption("1~720시간", wrap: false))) : null;
        var range = view is null ? null : Range(view, Wide);
        if (!Wide)
        {
            var stack = Ui.Stack(8, false, bar);
            if (custom is not null) stack.Children.Add(custom);
            if (range is not null) stack.Children.Add(range);
            return stack;
        }

        if (range is not null) range.HorizontalAlignment = HorizontalAlignment.Right;
        if (custom is null) return Ui.Columns(12, 1, bar, (FrameworkElement?)range ?? new Grid());
        // With the hours box beside the periods, the range goes to its own line, as the design wraps it.
        var lines = Ui.Stack(8, false, Ui.Columns(12, 2, bar, custom, new Grid()));
        if (range is not null) lines.Children.Add(range);
        return lines;
    }

    private static T Centered<T>(T element) where T : FrameworkElement
    {
        element.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }

    private static StackPanel Range(JsonObject view, bool right)
    {
        var start = QuotaFormat.Time(view["startedAt"])?.ToLocalTime();
        var end = QuotaFormat.Time(view["endedAt"])?.ToLocalTime();
        var text = start is { } from && end is { } to
            ? from.Date == to.Date ? $"{from:M월 d일 HH:mm} – {to:HH:mm}" : $"{from:M월 d일 HH:mm} – {to:M월 d일 HH:mm}"
            : "";
        var lines = Ui.Stack(0, false, Align(Ui.Caption(text, wrap: false), right));
        if (QuotaFormat.Flag(view["clipped"]) && QuotaFormat.Time(view["recordStartedAt"]) is { } first)
            lines.Children.Add(Align(Ui.Caption($"기록이 {first.ToLocalTime():M월 d일 HH:mm}부터 있어 {Elapsed(view)}으로 나눴어요."), right));
        lines.VerticalAlignment = VerticalAlignment.Center;
        return lines;
    }

    private static TextBlock Align(TextBlock text, bool right)
    {
        text.TextAlignment = right ? TextAlignment.Right : TextAlignment.Left;
        return text;
    }

    private static string Elapsed(JsonObject view)
    {
        var elapsed = QuotaFormat.Number(view["elapsedHours"]) ?? Hours(view);
        var minutes = (int)Math.Round(elapsed * 60);
        return minutes % 60 == 0 ? $"{minutes / 60}시간" : minutes < 60 ? $"{minutes}분" : $"{minutes / 60}시간 {minutes % 60}분";
    }

    // ---------- throughput ----------

    private Border Throughput(JsonObject view, IReadOnlyList<string> failed)
    {
        var rate = view["throughput"] as JsonObject;
        double Of(string key) => QuotaFormat.Number(rate?[key]) ?? 0;
        var cells = Cells(Wide ? 4 : 2,
            Cell("전체", Tokens(Of("total")), 28, "토큰/시간"),
            Cell("입력", Tokens(Of("input")), 18),
            Cell("출력", Tokens(Of("output")), 18),
            Cell("캐시 읽기 제외", Tokens(Of("uncached")), 18));
        AutomationProperties.SetName(cells, "시간당 평균");

        var active = AppState.Int(view["activeHours"]) ?? 0;
        var spanned = (int)Math.Ceiling((QuotaFormat.Number(view["elapsedHours"]) ?? 0) - 1e-9);
        var note = active == 0 ? "이 기간에는 기록이 없어요."
            : active >= spanned ? "기간 내내 요청이 있었어요."
            : $"요청이 있던 {active}시간만 치면 시간당 전체 {Tokens(Of("activeTotal"))} · 출력 {Tokens(Of("activeOutput"))}예요.";

        var inputParts = Composition("입력 구성",
            [(Fraction(Of("cacheRead"), Of("input")), "SyncBarCompositionMajor", "캐시 읽기", Of("cacheRead")),
             (Fraction(Of("cacheWrite"), Of("input")), "SyncBarCompositionMinor", "캐시 쓰기", Of("cacheWrite")),
             (Fraction(Of("uncachedInput"), Of("input")), "SyncBarCompositionNone", "캐시 없는 입력", Of("uncachedInput"))]);
        var reasoning = Fraction(Of("reasoning"), Of("output"));
        var outputParts = Composition("출력 구성",
            [(Of("output") <= 0 ? 0 : 1 - reasoning, "SyncBarCompositionMajor", "응답", Of("output") - Of("reasoning")),
             (reasoning, "SyncBarCompositionMinor", "추론", Of("reasoning"))]);
        var parts = Wide ? (FrameworkElement)EqualColumns(32, inputParts, outputParts) : Ui.Stack(16, false, inputParts, outputParts);

        var meta = $"경과 {Elapsed(view)} 기준" + (failed.Count == 0 ? "" : $" · {string.Join(", ", failed)} 빠짐");
        return Ui.Card(Ui.Stack(14, false, CardHead("시간당 평균", meta), cells, Ui.Caption(note),
            new Border { Style = Ui.Style("SyncBarDivider") }, parts));
    }

    private static double Fraction(double part, double whole) => whole <= 0 ? 0 : Math.Clamp(part / whole, 0, 1);

    private static StackPanel Composition(string title, IReadOnlyList<(double Share, string Style, string Label, double PerHour)> parts)
    {
        var bar = new Grid { Height = 6, ColumnSpacing = 2 };
        foreach (var (share, style, _, _) in parts)
        {
            // A part too small to see stays in the legend only.
            if (share < 0.005 || style == "SyncBarCompositionNone") continue;
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
            var piece = new Border { Style = Ui.Style(style), CornerRadius = new CornerRadius(3) };
            Grid.SetColumn(piece, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(piece);
        }

        AutomationProperties.SetName(bar, title + ": " + string.Join(", ", parts.Select(part => $"{part.Label} {Share(part.Share)}")));
        var legend = Ui.Wrap(16, parts.Select(part => (UIElement)Ui.Stack(6, true,
            Centered(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Style = Ui.Style(part.Style) }),
            Ui.Caption($"{part.Label} {Tokens(part.PerHour)} · {Share(part.Share)}", wrap: false))).ToArray());
        legend.LineSpacing = 4;
        return Ui.Stack(8, false, Ui.Caption(title), bar, legend);
    }

    // ---------- quota use per million tokens ----------

    private Border? Burn(JsonObject view)
    {
        var burn = view["burn"] as JsonObject;
        // Settings › 표시 decides which quotas show; with both off there is nothing to work out.
        var fiveShown = burn?["showFiveHour"] is not JsonValue five || QuotaFormat.Flag(five);
        var weekShown = burn?["showWeekly"] is not JsonValue week || QuotaFormat.Flag(week);
        if (!fiveShown && !weekShown) return null;
        var basis = Bases.FirstOrDefault(item => item.Id == Basis);
        if (basis.Id is null) basis = Bases[0];
        var per = basis.Id == TokenBurnBasis.Weighted ? "환산 1M 토큰당" : "1M 토큰당";
        var word = basis.Id == TokenBurnBasis.Weighted ? " 환산 토큰" : " 토큰";
        var head = CardHead("1M 토큰당 한도 소모", null, Ui.Stack(8, true, Centered(Ui.Caption("기준", wrap: false)), BasisPicker(basis.Label)));
        var body = Ui.Stack(12, false, head,
            Ui.Caption("고른 기간에 계정의 5시간·주간 사용률이 오른 만큼을 그동안 그 계정으로 쓴 토큰으로 나눈 평균이에요. 요금제마다 1%의 크기가 달라 계정별로 보여 줘요."));

        var accounts = (burn?["accounts"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var rated = accounts.Any(account => QuotaState(account["fiveHour"]) is "ok" or "low" || QuotaState(account["weekly"]) is "ok" or "low");
        var historyStart = QuotaFormat.Time(burn?["historyStartedAt"]);
        if (!rated && (historyStart is null || historyStart > QuotaFormat.Time(view["startedAt"])))
        {
            body.Children.Add(Waiting(historyStart));
            return Ui.Card(body);
        }

        if (Wide && accounts.Count > 0)
        {
            var heads = RateColumns(Ui.Caption("계정", wrap: false), fiveShown ? Ui.Caption("5시간 · " + per, wrap: false) : null,
                weekShown ? Ui.Caption("주간 · " + per, wrap: false) : null, new Grid());
            heads.Margin = new Thickness(17, 0, 17, -6);
            body.Children.Add(heads);
        }

        var rows = new StackPanel { Spacing = 4 };
        AutomationProperties.SetName(rows, "계정별 한도 소모");
        var current = accounts.FirstOrDefault(account => QuotaFormat.Flag(account["isCurrent"]));
        if (!_openedCurrent && AppState.Int(current?["profileId"]) is { } open) { OpenAccounts.Add(open); _openedCurrent = true; }
        foreach (var account in accounts) rows.Children.Add(Account(account, per, word, fiveShown, weekShown));
        body.Children.Add(rows);

        var basisTokens = QuotaFormat.Number(burn?["basisTokens"]) ?? 0;
        var attributed = QuotaFormat.Number(burn?["attributedTokens"]) ?? 0;
        var unregistered = QuotaFormat.Number(burn?["unregisteredTokens"]) ?? 0;
        var unpriced = QuotaFormat.Number(burn?["unpricedTokens"]) ?? 0;
        var share = basisTokens <= 0 ? 0 : attributed / basisTokens;
        var unknown = basisTokens <= 0 ? 0 : 1 - (attributed + unregistered) / basisTokens;
        var notes = Ui.Stack(2, false);
        if (basisTokens > 0)
        {
            // Under 1% is written as such; otherwise the whole percent, never rounded up to 100%.
            var shareText = share is > 0 and < 0.01 ? Share(share) : $"{Math.Floor(share * 100):0}%";
            var particle = shareText.EndsWith("미만", StringComparison.Ordinal) ? "을" : "를";
            notes.Children.Add(Ui.Caption($"이 기간 {basis.Short} {Tokens(basisTokens)} 중 {shareText}{particle} 계정에 연결해 계산했어요."
                + (unknown > 0.005 ? " 앱이 꺼져 있었거나 어느 계정이 썼는지 모르는 시간의 토큰은 뺐어요." : "")));
        }

        if (unregistered > 0)
            notes.Children.Add(Ui.Caption($"등록하지 않은 계정으로 쓴 {Tokens(unregistered)}{word}은 어느 계정의 한도에도 넣지 않았어요."));
        if (unpriced > 0)
            notes.Children.Add(Ui.Caption($"요금을 모르는 모델의 {Tokens(unpriced)} 토큰은 환산할 수 없어 그 구간을 뺐어요."));
        notes.Children.Add(Ui.Caption("Claude 데스크톱·웹 채팅이나 집계하지 않는 기기에서 쓴 양도 같은 한도를 줄여서, 실제보다 크게 나올 수 있어요."));
        var idle = (burn?["idleAccounts"] as JsonArray)?.Select(AppState.Str).OfType<string>().ToList() ?? [];
        if (idle.Count > 0)
            notes.Children.Add(Ui.Caption(idle.All(name => name.EndsWith(" 계정", StringComparison.Ordinal))
                ? string.Join("·", idle.Select(name => name[..^3])) + " 계정은 이 기간에 쓴 기록이 없어요."
                : "이 기간에 쓴 기록이 없는 계정: " + string.Join(", ", idle)));
        body.Children.Add(notes);
        return Ui.Card(body);
    }

    private static string? QuotaState(JsonNode? quota) => AppState.Str(quota?["state"]);

    private DropDownButton BasisPicker(string label)
    {
        var picker = Keep("basis", () =>
        {
            // Arrow keys only move through the choices; a click or Enter takes one and closes the list.
            var list = new ListView { SelectionMode = ListViewSelectionMode.Single, SingleSelectionFollowsFocus = false, IsItemClickEnabled = true, Width = 320 };
            foreach (var (id, title, _, description) in Bases)
                list.Items.Add(new ListViewItem { Tag = id, Padding = new Thickness(12, 8, 12, 9), Content = Ui.Stack(2, false, Ui.Text(title), Ui.Caption(description)) });
            AutomationProperties.SetName(list, "기준 토큰");
            var flyout = new Flyout { Content = list };
            var made = new DropDownButton { Flyout = flyout, MinWidth = 150 };
            list.ItemClick += (_, clicked) =>
            {
                var item = list.Items.OfType<ListViewItem>().FirstOrDefault(entry => ReferenceEquals(entry, clicked.ClickedItem) || ReferenceEquals(entry.Content, clicked.ClickedItem));
                flyout.Hide();
                if (item?.Tag is not string id || id == Basis) return;
                _chosenBasis = id;
                _ = ViewAsync();
            };
            flyout.Opening += (_, _) =>
            {
                _syncing = true;
                list.SelectedItem = list.Items.OfType<ListViewItem>().FirstOrDefault(item => (string)item.Tag == Basis);
                _syncing = false;
            };
            return made;
        });
        picker.Content = label;
        AutomationProperties.SetName(picker, $"기준 토큰, {label}");
        picker.IsEnabled = View is not null;
        return picker;
    }

    private static StackPanel Waiting(DateTimeOffset? since)
    {
        var icon = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Style = Ui.Style("SyncBarKindTileStyle"),
            Child = Ui.Glyph(Glyphs.History, 18, "SyncBarGlyphMuted"), VerticalAlignment = VerticalAlignment.Top,
        };
        var text = Ui.Stack(4, false, Ui.Text("한도 기록을 모으고 있어요", strong: true),
            Ui.Text("앱이 켜져 있는 동안 5분마다 계정별 5시간·주간 사용률을 기록해요. 토큰을 쓰는 동안 기록이 쌓이면 보통 몇 시간 안에 첫 값이 나와요."),
            Ui.Caption(since is { } at ? $"기록 시작 · {at.ToLocalTime():M월 d일 HH:mm} · 처리량과 합계는 그 전 기록으로도 바로 보여요." : "아직 기록이 없어요 · 처리량과 합계는 지난 기록으로 바로 보여요."));
        return Ui.Stack(16, true, icon, text);
    }

    // The account, its two rates and the chevron, on one line when wide and stacked when narrow.
    private UIElement Account(JsonObject account, string per, string word, bool fiveShown, bool weekShown)
    {
        var id = AppState.Int(account["profileId"]) ?? 0;
        var name = AppState.Str(account["name"]) ?? "계정";
        var current = QuotaFormat.Flag(account["isCurrent"]);
        var open = OpenAccounts.Contains(id);
        var plan = AppState.Str(account["plan"]);
        var detail = string.Join(" · ", new[] { plan, $"{Tokens(QuotaFormat.Number(account["attributedTokens"]) ?? 0)}{word}" }.Where(part => !string.IsNullOrEmpty(part)));
        var identity = Ui.Identity(new JsonObject { ["id"] = id, ["alias"] = name, ["email"] = name }, 32, detail,
            current ? Ui.Pill("사용 중", Ui.PillKind.Accent, Glyphs.Check) : null);
        var toggle = Ui.ExpandButton(open, name + " 근거", () => { if (!OpenAccounts.Remove(id)) OpenAccounts.Add(id); Redraw(); });
        var five = fiveShown ? Rate(account["fiveHour"] as JsonObject, "5시간 · " + per, word) : null;
        var week = weekShown ? Rate(account["weekly"] as JsonObject, "주간 · " + per, word) : null;

        FrameworkElement line;
        if (Wide) line = RateColumns(identity, five, week, toggle);
        else
        {
            var top = Ui.Columns(12, 0, identity, toggle);
            var stack = Ui.Stack(6, false, top);
            foreach (var rate in new[] { five, week }.OfType<FrameworkElement>()) stack.Children.Add(rate);

            line = stack;
        }

        var header = Ui.Row(line, current);
        AutomationProperties.SetName(header, name);
        return open ? Ui.Expander(header, Evidence(account, word, fiveShown, weekShown)) : header;
    }

    private Grid RateColumns(UIElement identity, UIElement? five, UIElement? week, UIElement end)
    {
        var grid = new Grid { ColumnSpacing = 20 };
        var parts = new (UIElement? Part, GridLength Width)[]
        {
            (identity, new GridLength(1.25, GridUnitType.Star)), (five, new GridLength(1, GridUnitType.Star)),
            (week, new GridLength(1, GridUnitType.Star)), (end, new GridLength(32)),
        };
        foreach (var (part, width) in parts)
        {
            if (part is not FrameworkElement element) continue;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(element, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(element);
        }

        return grid;
    }

    private FrameworkElement Rate(JsonObject? quota, string label, string word)
    {
        var state = QuotaState(quota) ?? "collecting";
        var rate = QuotaFormat.Number(quota?["ratePerMillion"]) ?? 0;
        var delta = Points(QuotaFormat.Number(quota?["deltaPercent"]) ?? 0);
        UIElement value = state switch
        {
            "ok" => Ui.Text(RateText(rate), 20, strong: true, wrap: false),
            "low" => Ui.Stack(4, true, Ui.Text("약 " + RateText(rate), 20, strong: true, secondary: true, wrap: false), Tip(Ui.Glyph(Glyphs.Info, 14, "SyncBarGlyphMuted"), RoughTip)),
            _ => Centered(Ui.Text("모으는 중", secondary: true, wrap: false)),
        };
        var caption = state switch
        {
            "ok" => $"100% ≈ {Tokens(1e8 / rate)}{word}",
            "low" => $"참고용 · 변화 {delta}%p",
            _ => $"변화 {delta}%p · 3%p부터 보여요",
        };
        if (Wide)
        {
            var cell = Ui.Stack(0, false, value, Ui.Caption(caption, wrap: false));
            AutomationProperties.SetName(cell, $"{label} {(state == "collecting" ? "모으는 중" : RateText(rate))}, {caption}");
            return cell;
        }

        // Narrow: the label, the value and what it means on one line, the meaning wrapping when it must.
        var line = Ui.Columns(8, 2, Centered(Ui.Caption(label, wrap: false)), value, Centered(Ui.Caption(caption)));
        line.ColumnDefinitions[0].Width = new GridLength(label.Contains("환산", StringComparison.Ordinal) ? 132 : 104);
        return line;
    }

    private static T Tip<T>(T element, string text) where T : FrameworkElement
    {
        ToolTipService.SetToolTip(element, text);
        AutomationProperties.SetHelpText(element, text);
        element.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }

    private static List<UIElement> Evidence(JsonObject account, string word, bool fiveShown, bool weekShown)
    {
        var rows = new List<UIElement>();
        string Line(JsonObject? quota) =>
            $"창 {AppState.Int(quota?["windows"]) ?? 0}개 · 사용률 +{Points(QuotaFormat.Number(quota?["deltaPercent"]) ?? 0)}%p ÷ {Tokens(QuotaFormat.Number(quota?["tokens"]) ?? 0)}{word}";
        var five = account["fiveHour"] as JsonObject;
        var week = account["weekly"] as JsonObject;
        if (fiveShown) rows.Add(Ui.SubRow(null, Labelled("5시간", Line(five)), 64));
        if (weekShown) rows.Add(Ui.SubRow(null, Labelled("주간", Line(week)), 64));
        var devices = (account["devices"] as JsonArray)?.Select(AppState.Str).OfType<string>().ToList() ?? [];
        if (devices.Count > 0) rows.Add(Ui.SubRow(null, Labelled("장치", string.Join(", ", devices)), 64));
        var cut = new List<string>();
        foreach (var (title, quota, shown) in new[] { ("5시간", five, fiveShown), ("주간", week, weekShown) })
            if (shown && QuotaFormat.Number(quota?["untrackedPercent"]) is > 0 and var untracked) cut.Add($"토큰 기록 없이 오른 {title} {Points(untracked)}%p");
        if (QuotaFormat.Flag(five?["planChanged"]) || QuotaFormat.Flag(week?["planChanged"])) cut.Add("요금제가 바뀌기 전 기록");
        if (cut.Count > 0) rows.Add(Ui.SubRow(null, Labelled("뺀 구간", string.Join(" · ", cut)), 64));
        return rows;
    }

    private static Grid Labelled(string label, string text)
    {
        var grid = Ui.Columns(8, 1, Ui.Caption(label, wrap: false), Ui.Text(text, 12));
        grid.ColumnDefinitions[0].Width = new GridLength(52);
        return grid;
    }

    // ---------- totals and devices ----------

    private Border Totals(JsonObject view)
    {
        var totals = view["totals"] as JsonObject;
        double Of(string key) => QuotaFormat.Number(totals?[key]) ?? 0;
        var cells = Ui.Wrap(28,
            Cell("전체 토큰", Tokens(Of("totalTokens")), 28, "토큰"),
            Cell("추정 비용 (USD)", Dollars(Of("estimatedCostUsd")), 28),
            Cell("입력", Tokens(Of("inputTokens")), 18),
            Cell("캐시 읽기", Tokens(Of("cachedInputTokens")), 18),
            Cell("출력", Tokens(Of("outputTokens")), 18));
        cells.LineSpacing = 16;
        AutomationProperties.SetName(cells, "기간 합계");
        return Ui.Card(cells);
    }

    private static List<string> FailedDevices(JsonObject view) =>
        (view["devices"] as JsonArray)?.OfType<JsonObject>()
            .Where(device => AppState.Str(device["error"]) is not null || !QuotaFormat.Flag(device["isReachable"]))
            .Select(device => AppState.Str(device["displayName"]) ?? "장치").ToList() ?? [];

    private InfoBar DeviceWarning(List<string> failed)
    {
        var key = _result?["collectedAt"]?.ToJsonString();
        var retry = Ui.Button("다시 시도", click: async (_, _) => await LoadAsync());
        retry.IsEnabled = !_loading;
        var bar = new InfoBar
        {
            IsOpen = _warningClosedFor != key, IsClosable = true, Severity = InfoBarSeverity.Warning,
            Title = $"{string.Join(", ", failed)} 기록을 읽지 못했어요",
            Message = "처리량과 합계는 나머지 장치만 셌어요. 그 장치가 계정을 쓰던 시간은 소모율에서 빼요.",
            ActionButton = retry,
        };
        bar.Closed += (_, _) => _warningClosedFor = key;
        return bar;
    }

    private UIElement Device(JsonObject device)
    {
        var id = AppState.Str(device["id"]) ?? "";
        var kind = AppState.Str(device["kind"]) ?? "local";
        var name = AppState.Str(device["displayName"]) ?? "장치";
        var failed = AppState.Str(device["error"]) is not null || !QuotaFormat.Flag(device["isReachable"]);
        var counted = QuotaFormat.Flag(device["hasMinutes"]);
        var models = (device["models"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var open = Open.Contains(id) && models.Count > 0 && !failed;
        var kindName = kind switch { "local" => "Windows", "ssh" => "SSH", _ => "WSL" };
        UIElement detail;
        if (failed)
        {
            var reason = Ui.Caption(AppState.Str(device["error"]) ?? "연결 실패 · 집계하지 못했어요");
            reason.Style = Ui.Style("SyncBarToneWarning");
            reason.FontSize = 12;
            detail = Ui.Columns(4, 1, Ui.Glyph(Glyphs.Warning, 12, "SyncBarGlyphWarning"), reason);
        }
        else
        {
            detail = Ui.Caption(kindName + (counted ? " · 집계 완료" : " · 기간별 기록 없음 · 장치를 다시 설치하면 보여요"));
        }

        var text = Ui.Stack(1, false, Ui.Text(name, wrap: false), detail);
        UIElement amount = failed || !counted ? Ui.Text("—", secondary: true)
            : Ui.Text($"{Tokens(QuotaFormat.Number(device["totalTokens"]) ?? 0)} 토큰 · {Dollars(QuotaFormat.Number(device["estimatedCostUsd"]) ?? 0)}", wrap: false);
        UIElement? action = failed
            ? Ui.Button("다시 시도", click: async (_, _) => await LoadAsync())
            : models.Count > 0 ? Ui.ExpandButton(open, name, () => { if (!Open.Remove(id)) Open.Add(id); Redraw(); }) : null;
        if (action is Button retry && failed) retry.IsEnabled = !_loading;
        var header = Ui.Row(Ui.Columns(16, 1, Ui.KindTile(kind == "local" ? Glyphs.Pc : Glyphs.Server), text, amount, action));
        return open ? Ui.Expander(header, [Ui.SubRow(null, Models(models, Wide), Wide ? 64 : 16)]) : header;
    }

    // Each model's share drawn against the largest; the bar is neutral, as colours here speak only of what is left.
    private static Grid Models(IReadOnlyList<JsonObject> models, bool wide)
    {
        var grid = new Grid { ColumnSpacing = wide ? 16 : 10, RowSpacing = 8 };
        foreach (var width in new[] { new GridLength(wide ? 180 : 116), new GridLength(1, GridUnitType.Star), new GridLength(72), new GridLength(64) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var largest = Math.Max(1, models.Max(model => QuotaFormat.Number(model["totalTokens"]) ?? 0));
        for (var row = 0; row < models.Count; row++)
        {
            var entry = models[row];
            var tokens = QuotaFormat.Number(entry["totalTokens"]) ?? 0;
            var modelName = AppState.Str(entry["model"]) ?? "unknown";
            var tiers = AppState.Str(entry["serviceTiers"]) ?? "default";
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var model = Ui.Mono(modelName);
            model.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTipService.SetToolTip(model, tiers == "default" ? modelName : $"{modelName} · {tiers}");
            var bar = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(3) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tokens, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(largest - tokens, GridUnitType.Star) });
            bar.Children.Add(new Border { Style = Ui.Style("SyncBarTokenBar") });
            var track = new Border { Style = Ui.Style("SyncBarTokenTrack") };
            Grid.SetColumn(track, 1);
            bar.Children.Add(track);
            AutomationProperties.SetName(bar, $"{modelName} {Tokens(tokens)} 토큰");
            var amount = Ui.Text(Tokens(tokens), 12, wrap: false);
            amount.HorizontalAlignment = HorizontalAlignment.Right;
            var cost = Ui.Caption(QuotaFormat.Flag(entry["isPriced"]) ? Dollars(QuotaFormat.Number(entry["estimatedCostUsd"]) ?? 0) : "요금 미상", wrap: false);
            cost.HorizontalAlignment = HorizontalAlignment.Right;
            var column = 0;
            foreach (var cell in new FrameworkElement[] { model, bar, amount, cost })
            {
                cell.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column++);
                grid.Children.Add(cell);
            }
        }

        return grid;
    }

    // ---------- parts ----------

    private static Grid CardHead(string title, string? meta, UIElement? end = null)
    {
        var grid = Ui.Columns(8, 0, Ui.Text(title, strong: true), meta is null ? null : Ui.Caption(meta, wrap: false), end);
        grid.MinHeight = 32;
        return grid;
    }

    private static StackPanel Cell(string title, string value, double size, string? unit = null)
    {
        var line = Ui.Stack(6, true, Ui.Text(value, size, strong: size > 20, wrap: false));
        if (unit is not null)
        {
            var caption = Ui.Caption(unit, wrap: false);
            caption.VerticalAlignment = VerticalAlignment.Bottom;
            caption.Margin = new Thickness(0, 0, 0, 5);
            line.Children.Add(caption);
        }

        var cell = Ui.Stack(2, false, Ui.Caption(title, wrap: false), line);
        cell.MinWidth = size > 20 ? 120 : 72;
        cell.VerticalAlignment = VerticalAlignment.Bottom;
        return cell;
    }

    // Cells in equal columns, the first a little wider, wrapping into rows of the given count.
    private static Grid Cells(int columns, params UIElement[] cells)
    {
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 16 };
        for (var i = 0; i < columns; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == 0 && columns > 2 ? 1.5 : 1, GridUnitType.Star) });
        for (var i = 0; i < cells.Length; i++)
        {
            if (i % columns == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow((FrameworkElement)cells[i], i / columns);
            Grid.SetColumn((FrameworkElement)cells[i], i % columns);
            grid.Children.Add(cells[i]);
        }

        return grid;
    }

    private static Grid EqualColumns(double spacing, params UIElement[] parts)
    {
        var grid = new Grid { ColumnSpacing = spacing };
        for (var i = 0; i < parts.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn((FrameworkElement)parts[i], i);
            grid.Children.Add(parts[i]);
        }

        return grid;
    }

    /// <summary>"5.2%p" rates: whole numbers from 10, one decimal from 1, else two significant figures, so a weekly rate never reads 0.</summary>
    public static string RateText(double rate)
    {
        if (rate <= 0) return "0%p";
        // The range is judged on the value as it will be written, so 0.996 reads 1.0%p and 9.96 reads 10%p.
        if (rate < 1) rate = Math.Round(rate, Math.Clamp(1 - (int)Math.Floor(Math.Log10(rate)), 1, 6));
        if (Math.Round(rate, 1) >= 10) return Math.Round(rate).ToString("0", CultureInfo.InvariantCulture) + "%p";
        if (rate >= 1) return rate.ToString("0.0", CultureInfo.InvariantCulture) + "%p";
        var decimals = Math.Clamp(1 - (int)Math.Floor(Math.Log10(rate)), 1, 6);
        return rate.ToString("F" + decimals, CultureInfo.InvariantCulture) + "%p";
    }

    /// <summary>A share of a whole: whole percent, a decimal below 1%, "0.1% 미만" below that.</summary>
    public static string Share(double fraction)
    {
        var percent = fraction * 100;
        if (percent <= 0) return "0%";
        if (percent < 0.1) return "0.1% 미만";
        if (Math.Round(percent, 1) < 1) return percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        return Math.Round(percent).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    private static string Points(double points) =>
        points == Math.Floor(points) ? points.ToString("#,##0", CultureInfo.InvariantCulture) : points.ToString("#,##0.0", CultureInfo.InvariantCulture);

    public static string Tokens(double count) => Tokens((long)Math.Round(count));

    /// <summary>"48.62M": two decimals for millions and billions, as the design reads them.</summary>
    public static string Tokens(long count) => count switch
    {
        >= 1_000_000_000 => (count / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (count / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + "M",
        >= 100_000 => (count / 1e3).ToString("0", CultureInfo.InvariantCulture) + "K",
        >= 10_000 => (count / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + "K",
        _ => count.ToString("N0", CultureInfo.InvariantCulture),
    };

    /// <summary>"$212", "$49.6", "$1.32": three significant figures.</summary>
    public static string Dollars(double amount) => "$" + amount switch
    {
        >= 100 => Math.Round(amount).ToString("N0", CultureInfo.InvariantCulture),
        >= 10 => amount.ToString("0.0", CultureInfo.InvariantCulture),
        _ => amount.ToString("0.00", CultureInfo.InvariantCulture),
    };
}
