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
using Windows.Graphics;

namespace ClaudeSyncBar.App.Tray;

/// <summary>
/// The quick view over the notification area: every account's quota at a
/// glance and a switch of this PC alone. The smart switch and anything that
/// changes every device go through the app. It hides when it loses focus.
/// </summary>
public sealed class TrayFlyout : Window
{
    private const int LogicalWidth = 380, LogicalHeight = 620;
    private const int MouseLowLevelHook = 14;
    private const uint ExtendedFrameBounds = 9;
    private const uint LeftDown = 0x0201, RightDown = 0x0204, MiddleDown = 0x0207, ExtraDown = 0x020B;
    private static readonly HookProcedure OutsideProcedure = OutsidePress;
    private static TrayFlyout? _watching;
    private readonly App _app;
    private readonly Border _surface = new();
    private readonly IntPtr _window;
    private IntPtr _hook;
    private bool _visible;
    private bool _disposing;
    private DateTimeOffset _lastAutomaticHide;
    private string? _pending;
    private (string Message, NoticeKind Kind)? _note;
    private (string Notice, JsonObject Preview, string Name)? _confirm;

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
        app.State.Changed += () => { if (_visible) Render(); };
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

    public void ShowNear(PointInt32? anchor)
    {
        _note = null;
        _confirm = null;
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
        var display = anchor is { } point ? DisplayArea.GetFromPoint(point, DisplayAreaFallback.Primary) : DisplayArea.Primary;
        var area = display.WorkArea;
        // Moving there first gives the window that display's DPI.
        AppWindow.Move(new PointInt32(area.X, area.Y));
        var scale = Math.Max(1.0, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);
        var margin = (int)Math.Round(12 * scale);
        var width = Math.Min((int)Math.Round(LogicalWidth * scale), area.Width - margin * 2);
        var height = Math.Min((int)Math.Round(LogicalHeight * scale), area.Height - margin * 2);
        var target = anchor ?? new PointInt32(area.X + area.Width, area.Y + area.Height);
        var x = Math.Clamp(target.X - width / 2, area.X + margin, area.X + area.Width - width - margin);
        // The taskbar is below the work area unless the icon is above it.
        var y = target.Y >= area.Y + area.Height / 2 ? area.Y + area.Height - height - margin : area.Y + margin;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void Render()
    {
        var state = _app.State;
        var root = new Grid { Padding = new Thickness(16, 14, 16, 12), RowSpacing = 12 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });

        var icon = new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.png")), Width = 20, Height = 20 };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var updated = state.Accounts.Select(account => QuotaFormat.Time(account["usage"]?["updatedAt"])).OfType<DateTimeOffset>().DefaultIfEmpty().Max();
        var subtitle = Ubuntu.Backend.BackendPlatform.LocalDeviceName + (updated == default ? "" : $" · {updated.ToLocalTime():HH:mm} 갱신")
            + (QuotaFormat.Flag(state.Settings?["autoSwitchEnabled"]) ? " · 자동 전환 켜짐" : "");
        var refresh = Ui.IconButton(Glyphs.Refresh, "사용량 새로고침", async (_, _) => await state.RefreshAsync());
        refresh.IsEnabled = !state.Busy && !state.RefreshRequested;
        var open = Ui.IconButton(Glyphs.OpenWindow, "앱 열기", (_, _) => { Hide(); _app.ShowMainWindow(); });
        root.Children.Add(Ui.Columns(10, 1, icon, Ui.Stack(0, false, Ui.Text("Claude SyncBar", strong: true, wrap: false), Ui.Caption(subtitle, wrap: false)), refresh, open));

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
        Grid.SetRow(title, 1);
        root.Children.Add(title);

        var list = new StackPanel { Spacing = 6 };
        AutomationProperties.SetName(list, "계정별 잔여량");
        foreach (var account in state.Accounts) list.Children.Add(Account(account));
        if (state.Accounts.Count == 0) list.Children.Add(Ui.Text("등록된 계정이 없어요. 앱에서 계정을 추가해 주세요.", secondary: true));
        var scroll = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0), Margin = new Thickness(0, 0, -8, 0) };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var footer = Footer();
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        _surface.Child = root;
    }

    private Border Account(JsonObject account)
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
            var change = Ui.Button("전환", click: async (_, _) => await SwitchAsync(id, name), tip: $"{name} 계정으로 이 PC 전환");
            change.IsEnabled = _pending is null && _confirm is null && !state.Busy && state.Error is null
                && AppState.Str(state.Operation?["state"]) != "recoveryRequired";
            AutomationProperties.SetName(change, $"{name} 계정으로 이 PC 전환");
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

    private StackPanel Footer()
    {
        var state = _app.State;
        var footer = new StackPanel { Spacing = 10 };
        if (_confirm is { } confirm)
        {
            var go = Ui.Button("전환", accent: true, click: async (_, _) => await ApplyAsync(confirm.Preview));
            var cancel = Ui.Button("취소", click: (_, _) => { _confirm = null; Render(); });
            var text = Ui.Stack(4, false, Ui.Text($"{confirm.Name}(으)로 이 PC를 전환할까요?", strong: true), Ui.Caption(confirm.Notice));
            footer.Children.Add(new Border
            {
                Style = Ui.Style("SyncBarCardStyle"), Padding = new Thickness(12),
                Child = Ui.Stack(10, false, text, Ui.Stack(8, true, go, cancel)),
            });
        }

        var working = _pending ?? (state.Busy ? AppState.Str(state.Operation?["message"]) ?? state.BusyLabel : null);
        if (working is not null)
            footer.Children.Add(Ui.Columns(8, 1, new ProgressRing { IsActive = true, Width = 14, Height = 14 }, Ui.Caption(working)));
        else if (_note is { } note)
        {
            var message = Ui.Caption(note.Message);
            if (note.Kind is NoticeKind.Error or NoticeKind.Warning)
            {
                message.Style = Ui.Style(note.Kind == NoticeKind.Error ? "SyncBarToneCritical" : "SyncBarToneWarning");
                message.FontSize = 12;
            }

            footer.Children.Add(message);
        }

        var smart = Ui.Button("스마트 전환", Glyphs.Lightning, accent: true, click: async (_, _) =>
        {
            if (_app.Tray is { } tray) await tray.SmartSwitchAsync();
        }, tip: "Max 20x 계정 중 주간·5시간 한도가 모두 남은 계정에서 주간 초기화가 가장 가까운 계정으로 모든 활성 장치를 전환해요.");
        smart.HorizontalAlignment = HorizontalAlignment.Stretch;
        smart.IsEnabled = !state.Busy && state.Error is null && state.Accounts.Count > 1;
        var open = Ui.Button("앱 열기", click: (_, _) => { Hide(); _app.ShowMainWindow(); });
        footer.Children.Add(Ui.Columns(8, 0, smart, open));
        var quit = Ui.IconButton(Glyphs.Power, "Claude SyncBar 완전히 종료", (_, _) => _app.Quit());
        footer.Children.Add(Ui.Columns(8, 0, Ui.Caption("전환 버튼은 이 PC에만 적용돼요. 모든 장치는 스마트 전환이나 앱에서 바꿔요."), quit));
        return footer;
    }

    // Switches this PC only. Anything that would change another machine stops it.
    private async Task SwitchAsync(int profileId, string name)
    {
        if (_pending is not null) return;
        _note = null;
        _pending = $"{name}(으)로 바꿀 준비를 하고 있어요…";
        Render();
        try
        {
            var preview = await _app.State.Client.CallAsync("preview", new { profileId, deviceId = "local" }) as JsonObject
                ?? throw new ServiceError("invalid_preview", "전환할 수 없어요. 앱에서 상태를 확인해 주세요.");
            if (!QuotaFormat.Flag(preview["canApply"]))
                throw new ServiceError("blocked", AppState.Str(preview["blockingReason"]) ?? "전환할 수 없어요. 앱에서 상태를 확인해 주세요.");
            var targets = (preview["targets"] as JsonArray)?.OfType<JsonObject>().Where(target => QuotaFormat.Flag(target["included"])).ToList() ?? [];
            if (targets.Count != 1 || AppState.Str(targets[0]["id"]) != "local" || AppState.Int(preview["profileId"]) != profileId)
                throw new ServiceError("scope", "이 PC에만 적용되는 전환인지 확인하지 못했어요.");
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

    private async Task ApplyAsync(JsonObject preview)
    {
        _confirm = null;
        _pending = "이 PC의 계정을 바꾸고 있어요…";
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
