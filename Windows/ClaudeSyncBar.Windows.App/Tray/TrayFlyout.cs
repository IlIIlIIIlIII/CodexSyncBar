using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ClaudeSyncBar.App.Controls;
using ClaudeSyncBar.App.Model;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;

namespace ClaudeSyncBar.App.Tray;

/// <summary>
/// The quick view over the notification area: every account's quota at a
/// glance and a switch of every active device, this PC with its SSH hosts and
/// WSL distributions, as the app's apply does. The smart switch asks in the
/// app, where there is room to show its choice. It hides when it loses focus.
/// </summary>
public sealed class TrayFlyout : Window
{
    private const int LogicalWidth = 380;
    private const int MouseLowLevelHook = 14;
    private const uint ExtendedFrameBounds = 9;
    private const uint LeftDown = 0x0201, RightDown = 0x0204, MiddleDown = 0x0207, ExtraDown = 0x020B;
    private static readonly HookProcedure OutsideProcedure = OutsidePress;
    private static TrayFlyout? _watching;
    private readonly App _app;
    private readonly Border _surface = new();
    // Built once and changed in place. Drawn anew on every look during a job, the ring started
    // over and the disabled smart switch faded in from its enabled colour (the accent button's
    // template animates its background), as if the refresh stopped and went on again.
    private readonly TextBlock _subtitle;
    private readonly Button _refresh;
    private readonly Border _title = new();
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly Border _confirmCard;
    private readonly ProgressRing _ring = new() { Width = 14, Height = 14 };
    private readonly TextBlock _workingText;
    private readonly Grid _working;
    private readonly Border _noteHost = new();
    private readonly Button _smart;
    private readonly TextBlock _scope;
    private string? _signature;
    private readonly IntPtr _window;
    private IntPtr _hook;
    private bool _visible;
    private bool _disposing;
    private DateTimeOffset _lastAutomaticHide;
    private PointInt32? _anchor;
    private string? _pending;
    private (string Message, NoticeKind Kind)? _note;
    private (string Notice, JsonObject Preview, string Name)? _confirm;
    private DateTimeOffset _timelineAsked;

    public TrayFlyout(App app)
    {
        _app = app;
        Content = _surface;
        _window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SystemBackdrop = new DesktopAcrylicBackdrop();
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Title = "Claude SyncBar 빠른 보기";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            // A border without a title bar keeps the rounded corners and shadow of a Windows 11 flyout.
            presenter.SetBorderAndTitleBar(true, false);
        }

        if (app.Options.Theme is "light" or "dark")
            _surface.RequestedTheme = app.Options.Theme == "light" ? ElementTheme.Light : ElementTheme.Dark;
        // A capture has no acrylic behind it; the colour Windows falls back to stands in.
        if (app.Options.CapturePath is not null)
            _surface.Background = new SolidColorBrush(app.Options.Theme == "dark" ? Microsoft.UI.ColorHelper.FromArgb(255, 44, 44, 44) : Microsoft.UI.ColorHelper.FromArgb(255, 238, 238, 238));
        _surface.KeyDown += (_, args) => { if (args.Key == global::Windows.System.VirtualKey.Escape) Hide(); };
        _surface.ActualThemeChanged += (_, _) => Redraw();

        var icon = new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.png")), Width = 20, Height = 20 };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        _subtitle = Ui.Caption("", wrap: false);
        _refresh = Ui.IconButton(Glyphs.Refresh, "사용량 새로고침", async (_, _) => await _app.State.RefreshAsync());
        var openApp = Ui.IconButton(Glyphs.OpenWindow, "앱 열기", (_, _) => { Hide(); _app.ShowMainWindow(); });
        var header = Ui.Columns(10, 1, icon, Ui.Stack(0, false, Ui.Text("Claude SyncBar", strong: true, wrap: false), _subtitle), _refresh, openApp);

        AutomationProperties.SetName(_list, "계정별 잔여량");
        var scroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0), Margin = new Thickness(0, 0, -8, 0) };

        _confirmCard = new Border { Style = Ui.Style("SyncBarCardStyle"), Padding = new Thickness(12), Visibility = Visibility.Collapsed };
        _workingText = Ui.Caption("");
        _working = Ui.Columns(8, 1, _ring, _workingText);
        _working.Visibility = Visibility.Collapsed;
        _noteHost.Visibility = Visibility.Collapsed;
        _smart = Ui.Button("스마트 전환", Glyphs.Lightning, accent: true, click: async (_, _) =>
        {
            if (_app.Tray is { } tray) await tray.SmartSwitchAsync();
        }, tip: "스마트 전환 후보 중 주간 한도가 1% 이상 남은 계정에서 주간 초기화가 가장 가까운 계정으로 모든 활성 장치를 전환해요. 후보는 계정 화면에서 골라요.");
        _smart.HorizontalAlignment = HorizontalAlignment.Stretch;
        var openFooter = Ui.Button("앱 열기", click: (_, _) => { Hide(); _app.ShowMainWindow(); });
        var quit = Ui.IconButton(Glyphs.Power, "Claude SyncBar 완전히 종료", (_, _) => _app.Quit());
        _scope = Ui.Caption("");
        var footer = Ui.Stack(10, false, _confirmCard, _working, _noteHost, Ui.Columns(8, 0, _smart, openFooter), Ui.Columns(8, 0, _scope, quit));

        var root = new Grid { Padding = new Thickness(16, 14, 16, 12), RowSpacing = 12 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        var row = 0;
        foreach (var part in new FrameworkElement[] { header, _title, scroll, footer })
        {
            Grid.SetRow(part, row++);
            root.Children.Add(part);
        }

        _surface.Child = root;
        root.Loaded += (_, _) => { if (_visible) Fit(); };
        Activated += (_, args) =>
        {
            if (!_visible || _disposing || args.WindowActivationState != WindowActivationState.Deactivated || _app.Options.CapturePath is not null) return;
            _lastAutomaticHide = DateTimeOffset.UtcNow;
            Hide();
        };
        AppWindow.Closing += (_, args) =>
        {
            if (_disposing) return;
            args.Cancel = true;
            Hide();
        };
        app.State.Changed += () =>
        {
            if (!_visible) return;
            // The week's strip follows the clock: read again once a minute while the quick view is open.
            if (DateTimeOffset.UtcNow - _timelineAsked >= TimeSpan.FromMinutes(1)) AskTimeline();
            Render();
        };
        app.State.Notice += (message, kind) =>
        {
            if (!_visible) return;
            _note = (message, kind);
            Render();
        };
    }

    public UIElement Surface => _surface;

    public void Toggle(PointInt32? anchor)
    {
        if (_visible)
        {
            Hide();
            return;
        }

        // A click on the icon first takes focus from this window, which hides it; that click must not open it again.
        if (DateTimeOffset.UtcNow - _lastAutomaticHide < TimeSpan.FromMilliseconds(350)) return;
        ShowNear(anchor);
    }

    // The answer comes back through the state's Changed, which draws the strip.
    private void AskTimeline()
    {
        _timelineAsked = DateTimeOffset.UtcNow;
        _ = _app.State.RefreshTimelineAsync();
    }

    public void ShowNear(PointInt32? anchor)
    {
        _note = null;
        _confirm = null;
        _signature = null;
        AskTimeline();
        Render();
        Position(anchor);
        AppWindow.Show();
        _visible = true;
        Activate();
        Watch();
    }

    public void Hide()
    {
        if (!_visible) return;
        _visible = false;
        Unwatch();
        AppWindow.Hide();
    }

    public void Dispose()
    {
        _disposing = true;
        Unwatch();
        Close();
    }

    // A press anywhere outside closes the quick view. Losing focus alone is not enough:
    // opened from the taskbar Windows may never give it focus, so it would never lose it.
    private void Watch()
    {
        if (_hook != IntPtr.Zero || _app.Options.CapturePath is not null) return;
        _watching = this;
        _hook = SetWindowsHookEx(MouseLowLevelHook, OutsideProcedure, GetModuleHandle(null), 0);
    }

    private void Unwatch()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    // Runs on this window's thread for every mouse event while the quick view is open, so it only looks and hands over.
    private static IntPtr OutsidePress(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && _watching is { _visible: true } flyout && (uint)message.ToInt64() is LeftDown or RightDown or MiddleDown or ExtraDown)
        {
            var point = Marshal.PtrToStructure<NativePoint>(data);
            if (!flyout.Contains(point))
                flyout.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!flyout._visible) return;
                    // The press may land on the icon that opened it; that click must not open it again.
                    flyout._lastAutomaticHide = DateTimeOffset.UtcNow;
                    flyout.Hide();
                });
        }

        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    // The visible frame, without the invisible resize border GetWindowRect counts.
    private bool Contains(NativePoint point)
    {
        if (DwmGetWindowAttribute(_window, ExtendedFrameBounds, out var frame, (uint)Marshal.SizeOf<NativeRect>()) != 0
            && !GetWindowRect(_window, out frame))
            return true;
        return point.X >= frame.Left && point.X < frame.Right && point.Y >= frame.Top && point.Y < frame.Bottom;
    }

    // Above the icon, inside the work area of its display.
    private void Position(PointInt32? anchor)
    {
        _anchor = anchor;
        var display = anchor is { } point ? DisplayArea.GetFromPoint(point, DisplayAreaFallback.Primary) : DisplayArea.Primary;
        // Moving there first gives the window that display's DPI.
        AppWindow.Move(new PointInt32(display.WorkArea.X, display.WorkArea.Y));
        Fit();
    }

    // As tall as its content, so every account shows without scrolling; only a list taller than the work area scrolls.
    private void Fit()
    {
        var display = _anchor is { } point ? DisplayArea.GetFromPoint(point, DisplayAreaFallback.Primary) : DisplayArea.Primary;
        var area = display.WorkArea;
        var scale = Math.Max(1.0, GetDpiForWindow(_window) / 96.0);
        var margin = (int)Math.Round(12 * scale);
        var width = Math.Min((int)Math.Round(LogicalWidth * scale), area.Width - margin * 2);
        // The frame around the client area, so the content gets the whole height it asks for.
        var frameWidth = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        var frameHeight = AppWindow.Size.Height - AppWindow.ClientSize.Height;
        _surface.Measure(new Size((width - frameWidth) / scale, double.PositiveInfinity));
        var height = Math.Min((int)Math.Ceiling(_surface.DesiredSize.Height * scale) + frameHeight, area.Height - margin * 2);
        // The next layout pass measures against the window again.
        _surface.InvalidateMeasure();
        var target = _anchor ?? new PointInt32(area.X + area.Width, area.Y + area.Height);
        var x = Math.Clamp(target.X - width / 2, area.X + margin, area.X + area.Width - width - margin);
        // The taskbar is below the work area unless the icon is above it.
        var y = target.Y >= area.Y + area.Height / 2 ? area.Y + area.Height - height - margin : area.Y + margin;
        if (AppWindow.Position.X == x && AppWindow.Position.Y == y && AppWindow.Size.Width == width && AppWindow.Size.Height == height) return;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    // What the quick view shows; while it is unchanged nothing is drawn again.
    private string Signature()
    {
        var state = _app.State;
        var snapshot = (JsonObject)state.Snapshot.DeepClone();
        snapshot.Remove("updatedAt");
        // Reset times count down in minutes.
        return string.Join("|", snapshot.ToJsonString(), state.RefreshRequested, state.ConnectionError, _pending, _note?.Message, _note?.Kind,
            _confirm?.Name, AppState.Str(_confirm?.Preview["previewId"]), DateTime.Now.ToString("HH:mm"), state.Timeline?.ToJsonString());
    }

    private void Redraw()
    {
        _signature = null;
        if (_visible) Render();
    }

    private void Render()
    {
        var signature = Signature();
        if (signature == _signature) return;
        _signature = signature;
        var state = _app.State;
        // A refresh asked for counts as under way: until the service says so, nothing else starts.
        var busy = state.Busy || state.RefreshRequested;
        var updated = state.Accounts.Select(account => QuotaFormat.Time(account["usage"]?["updatedAt"])).OfType<DateTimeOffset>().DefaultIfEmpty().Max();
        _subtitle.Text = Ubuntu.Backend.BackendPlatform.LocalDeviceName + (updated == default ? "" : $" · {updated.ToLocalTime():HH:mm} 갱신")
            + (QuotaFormat.Flag(state.Settings?["autoSwitchEnabled"]) ? " · 자동 전환 켜짐" : "");
        _refresh.IsEnabled = !busy;
        _title.Child = Heading();
        _list.Children.Clear();
        foreach (var account in state.Accounts) _list.Children.Add(Account(account, busy));
        if (state.Accounts.Count == 0) _list.Children.Add(Ui.Text("등록된 계정이 없어요. 앱에서 계정을 추가해 주세요.", secondary: true));
        UpdateFooter(busy);
        // A notice, a confirmation or another account changes the height while it is open.
        if (_visible) Fit();
    }

    private StackPanel Heading()
    {
        var state = _app.State;
        var now = DateTimeOffset.UtcNow;
        var weekly = state.VisibleQuotas.Contains("weekly");
        var pool = WeeklyPool.Of(state.Accounts, now, state.ConnectionError is not null);
        var count = $"{state.Accounts.Count}개 계정" + (pool.Remaining is { } left && weekly ? $" · {WeeklyPool.Label} {QuotaFormat.Percent(left)}" : "");
        var title = Ui.Stack(4, false, Ui.Columns(8, 0, Ui.Text("계정별 잔여량", strong: true), Ui.Caption(count, wrap: false)));
        // The account whose week resets first, counted down: the sum grows again then.
        if (weekly && pool.Countdown(now) is { } countdown)
        {
            var timer = Ui.Caption($"{countdown} · {pool.NextResetAccount} · {QuotaFormat.Clock(pool.NextReset!.Value, now)}", wrap: false);
            AutomationProperties.SetName(timer, $"{countdown}, {pool.NextResetAccount}, {QuotaFormat.Date(pool.NextReset.Value)}");
            title.Children.Add(Ui.Columns(6, 1, Ui.Glyph(Glyphs.Clock, 12, "SyncBarGlyphMuted"), timer));
        }

        if (Strip(state.Timeline) is { } strip) title.Children.Add(strip);
        return title;
    }

    // This week's plan in one row: a band per account in reset order, the now line across it, and whose turn it is.
    // Only when this week's timeline has been read lately and has a plan; the page has the record beside it. A view
    // kept from a reading long ago, or from before a failed one, would show an old now and an old turn.
    private static StackPanel? Strip(JsonObject? view)
    {
        var at = DateTimeOffset.UtcNow;
        if (view is null || AppState.Str(view["state"]) != "ok" || AppState.Int(view["week"]) != 0 || !TimelineText.Current(view, at)) return null;
        var days = TimelineText.Items(view["days"]);
        var segments = TimelineText.Items(view["segments"]);
        if (days.Count == 0 || segments.Count == 0) return null;

        var count = Ui.Caption(TimelineText.StripCount(view), wrap: false);
        count.HorizontalAlignment = HorizontalAlignment.Right;
        var head = Ui.Columns(8, 1, Ui.Text("이번 주 구간", 12, strong: true, wrap: false), count);

        // The now line comes down over the letters and the bands. A letter it would run through steps aside; a band
        // stays one piece with its name at its left edge, and the line crosses it, so it reads as no extra segment.
        var nowAt = QuotaFormat.Number(view["nowAt"]);
        var letters = new TimelinePanel { Height = 14, Avoid = nowAt ?? double.NaN };
        for (var i = 0; i < days.Count; i++)
        {
            var today = TimelineText.IsToday(days[i], at);
            var letter = Ui.Text(TimelineText.DayLetter(days[i]), 11, strong: today, secondary: !today, wrap: false);
            letter.VerticalAlignment = VerticalAlignment.Center;
            letters.Text(letter, letters.Band(new Border(), (double)i / days.Count, 1.0 / days.Count), 4);
        }

        var bands = new TimelinePanel { Height = 24, Gap = 2 };
        var spoken = new List<string>();
        foreach (var segment in segments)
        {
            var name = AppState.Str(segment["name"]) ?? "계정";
            var label = Ui.Text(name, 11, wrap: false);
            label.VerticalAlignment = VerticalAlignment.Center;
            var band = new Border { Style = Ui.Style("SyncBarTimelineStrip") };
            var id = AppState.Int(segment["profileId"]);
            var range = QuotaFormat.Time(segment["startsAt"]) is { } starts && QuotaFormat.Time(segment["endsAt"]) is { } ends
                ? $"{name} · {TimelineText.EdgeText(view, id, starts)} ~ {TimelineText.EdgeText(view, id, ends)}" : name;
            ToolTipService.SetToolTip(band, range);
            AutomationProperties.SetName(band, range);
            spoken.Add(range);
            bands.Band(band, QuotaFormat.Number(segment["at"]) ?? 0, QuotaFormat.Number(segment["width"]) ?? 0);
            bands.Text(label, band, 4, 18);
        }

        var chart = new Grid { RowSpacing = 4 };
        chart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chart.Children.Add(letters);
        Grid.SetRow(bands, 1);
        chart.Children.Add(bands);
        if (nowAt is { } along)
        {
            var now = new TimelinePanel();
            var line = now.Mark(new Border { Width = 2, CornerRadius = new CornerRadius(1), Style = Ui.Style("SyncBarTimelineNow") }, along);
            var time = QuotaFormat.Time(view["now"]) is { } read ? $"지금 {read.ToLocalTime():HH:mm}" : "지금";
            ToolTipService.SetToolTip(line, time);
            AutomationProperties.SetName(line, time);
            Grid.SetRowSpan(now, 2);
            chart.Children.Add(now);
        }

        var turn = Ui.Stack(0, false, Ui.Text(TimelineText.TurnTitle(view), 12, strong: true));
        if (TimelineText.StripCaption(view) is { Length: > 0 } caption) turn.Children.Add(Ui.Caption(caption));
        var strip = Ui.Stack(6, false, head, chart, turn);
        // The heading's lines sit 4 apart; the strip is a part of its own, as far from them as the cards are from it.
        strip.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetName(strip, $"이번 주 구간, {TimelineText.StripCount(view)}, {string.Join(", ", spoken)}, {TimelineText.TurnTitle(view)}");
        return strip;
    }

    private Border Account(JsonObject account, bool busy)
    {
        var state = _app.State;
        var id = AppState.Int(account["id"]) ?? 0;
        var name = AppState.AccountName(account);
        var current = id == state.ActiveProfileId;
        var usage = QuotaFormat.Account(account, state.VisibleQuotas, DateTimeOffset.UtcNow, unavailable: state.ConnectionError is not null);
        UIElement action;
        if (current)
        {
            var label = Ui.Caption("사용 중", wrap: false);
            label.Style = Ui.Style("SyncBarAccentText");
            label.FontSize = 12;
            action = Ui.Stack(4, true, Ui.Glyph(Glyphs.Check, 12, "SyncBarGlyphAccent"), label);
        }
        else if (AppState.NeedsLogin(account))
        {
            action = Ui.Pill("재로그인 필요", Ui.PillKind.Caution, Glyphs.Lock);
        }
        else
        {
            var change = Ui.Button("전환", click: async (_, _) => await SwitchAsync(id, name), tip: $"{name} 계정으로 모든 장치 전환");
            change.IsEnabled = _pending is null && _confirm is null && !busy && state.Error is null
                && AppState.Str(state.Operation?["state"]) != "recoveryRequired";
            AutomationProperties.SetName(change, $"{name} 계정으로 모든 장치 전환");
            action = change;
        }

        var body = Ui.Stack(8, false, Ui.Columns(10, 0, Ui.Identity(account, 32, AppState.Str(account["email"]) is { } email && email != name ? email : ""), action));
        foreach (var quota in usage.Quotas)
        {
            var row = new Grid { ColumnSpacing = 8 };
            // Room for "10.24 05:00 초기화": the weekly reset says the hour too.
            foreach (var width in new[] { new GridLength(36), new GridLength(1, GridUnitType.Star), new GridLength(44), new GridLength(108) })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            var meter = Ui.Meter(quota, 4);
            meter.VerticalAlignment = VerticalAlignment.Center;
            var value = Ui.ToneText(quota.Value, quota.Tone, 13);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            var caption = Ui.Caption(quota.Short, wrap: false);
            var column = 0;
            foreach (var cell in new FrameworkElement[] { Ui.Caption(quota.Title, wrap: false), meter, value, caption })
            {
                cell.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(cell, column++);
                row.Children.Add(cell);
            }

            body.Children.Add(row);
        }

        if (usage.Status.Length > 0 && !AppState.NeedsLogin(account)) body.Children.Add(Ui.Caption(usage.Status, wrap: false));
        var card = Ui.Row(body, current);
        card.Padding = new Thickness(12, 10, 12, 10);
        card.MinHeight = 0;
        AutomationProperties.SetName(card, string.Join(", ", new[] { name, current ? "사용 중" : "" }.Concat(usage.Quotas.Select(quota => quota.Spoken)).Where(part => part.Length > 0)));
        return card;
    }

    private void UpdateFooter(bool busy)
    {
        var state = _app.State;
        _confirmCard.Child = null;
        if (_confirm is { } confirm)
        {
            var go = Ui.Button("전환", accent: true, click: async (_, _) => await ApplyAsync(confirm.Preview));
            var cancel = Ui.Button("취소", click: (_, _) => { _confirm = null; Render(); });
            var text = Ui.Stack(4, false, Ui.Text($"{confirm.Name}(으)로 모든 장치를 전환할까요?", strong: true), Ui.Caption(confirm.Notice));
            _confirmCard.Child = Ui.Stack(10, false, text, Ui.Stack(8, true, go, cancel));
        }
        _confirmCard.Visibility = _confirm is null ? Visibility.Collapsed : Visibility.Visible;

        // One label from the click to the end of the refresh; the job's own message is generic.
        var working = _pending ?? (state.Refreshing ? "새로고침 중…" : state.Busy ? AppState.Str(state.Operation?["message"]) ?? state.BusyLabel : null);
        _workingText.Text = working ?? "";
        _working.Visibility = working is null ? Visibility.Collapsed : Visibility.Visible;
        _ring.IsActive = working is not null;

        _noteHost.Child = null;
        if (working is null && _note is { } note)
        {
            var message = Ui.Caption(note.Message);
            if (note.Kind is NoticeKind.Error or NoticeKind.Warning)
            {
                message.Style = Ui.Style(note.Kind == NoticeKind.Error ? "SyncBarToneCritical" : "SyncBarToneWarning");
                message.FontSize = 12;
            }

            _noteHost.Child = message;
        }
        _noteHost.Visibility = _noteHost.Child is null ? Visibility.Collapsed : Visibility.Visible;

        _smart.IsEnabled = !busy && state.Error is null && state.Accounts.Count > 1;
        var others = state.Devices.Count(device => AppState.Str(device["id"]) != "local" && QuotaFormat.Flag(device["enabled"]));
        _scope.Text = others > 0 ? $"전환하면 이 PC와 활성 장치 {others}대가 함께 바뀌어요." : "전환하면 이 PC의 계정이 바뀌어요. 다른 장치는 앱에서 추가해요.";
    }

    // Every active device, as the app's apply: this PC, its SSH hosts and WSL distributions.
    private async Task SwitchAsync(int profileId, string name)
    {
        if (_pending is not null) return;
        _note = null;
        _pending = $"{name}(으)로 바꿀 준비를 하고 있어요…";
        Render();
        try
        {
            var preview = await _app.State.Client.CallAsync("preview", new { profileId }) as JsonObject
                ?? throw new ServiceError("invalid_preview", "전환할 수 없어요. 앱에서 상태를 확인해 주세요.");
            if (!QuotaFormat.Flag(preview["canApply"]))
                throw new ServiceError("blocked", Blocked(preview));
            if (AppState.Int(preview["profileId"]) != profileId)
                throw new ServiceError("scope", "전환할 계정을 확인하지 못했어요.");
            _pending = null;
            // Restarting Claude Desktop stops its sessions for a moment; the user says when.
            if (AppState.Str(preview["desktopNotice"]) is { Length: > 0 } notice)
            {
                _confirm = (notice, preview, name);
                Render();
                return;
            }

            await ApplyAsync(preview);
        }
        catch (ServiceError error)
        {
            _pending = null;
            _note = (error.Message, NoticeKind.Error);
            Render();
        }
    }

    // The quick view lists no devices, so a device that holds the switch back is named.
    private static string Blocked(JsonObject preview)
    {
        var reason = AppState.Str(preview["blockingReason"]) ?? "전환할 수 없어요. 앱에서 상태를 확인해 주세요.";
        var held = (preview["targets"] as JsonArray)?.OfType<JsonObject>()
            .Where(target => QuotaFormat.Flag(target["included"]) && !QuotaFormat.Flag(target["skipOnSwitchFailure"])
                && !(QuotaFormat.Flag(target["isReachable"]) && (AppState.Str(target["kind"]) == "local" || AppState.Str(target["status"]) == "ready")))
            .Select(target => AppState.Str(target["displayName"])).OfType<string>().ToList() ?? [];
        return held.Count == 0 ? reason : $"{reason} ({string.Join(", ", held)})";
    }

    private async Task ApplyAsync(JsonObject preview)
    {
        _confirm = null;
        _pending = "모든 장치의 계정을 바꾸고 있어요…";
        Render();
        await _app.State.ApplyPreviewAsync(preview);
        _pending = null;
        Render();
    }

    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

    // The start of MSLLHOOKSTRUCT: the pointer, in physical screen pixels.
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hook, HookProcedure procedure, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out NativeRect value, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}
