using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ClaudeSyncBar.App.Model;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace ClaudeSyncBar.App.Tray;

/// <summary>
/// The notification-area icon. WinUI has none, so it is the shell's own
/// (Shell_NotifyIcon) on a hidden window. A click opens the quick view, the
/// right button the menu; the icon and its tooltip follow the account in use.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifShowTip = 0x80;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NotifyIconVersion4 = 4;
    private const uint WmTray = 0x8000 + 1;
    private const uint WmContextMenu = 0x007B, WmPowerBroadcast = 0x0218, WmNull = 0, WmDestroy = 0x0002;
    private const uint NinSelect = 0x400, NinKeySelect = 0x401;
    private const nint PbtResumeSuspend = 0x7, PbtResumeAutomatic = 0x12;
    private const uint TpmReturnCmd = 0x100, TpmRightButton = 0x2, TpmBottomAlign = 0x20;
    private const uint MfString = 0, MfSeparator = 0x800, MfGrayed = 0x1;
    private const uint ImageIcon = 1, LrLoadFromFile = 0x10;
    private const int SmCxSmIcon = 49, SmCySmIcon = 50;
    private static readonly uint WmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private static readonly WndProcDelegate Procedure = WindowProcedure;

    private enum Command : uint { Open = 1, QuickView, Refresh, SmartSwitch, Settings, Quit, HideTaskbar }

    private readonly App _app;
    private readonly DispatcherQueue _queue;
    private readonly IntPtr _window;
    private readonly string _className;
    private readonly IntPtr _instance;
    private readonly Dictionary<Tone, IntPtr> _icons = [];
    private GCHandle _self;
    private IntPtr _icon;
    private string _tip = "Claude SyncBar";
    private TrayFlyout? _flyout;
    private bool _disposed;

    public TrayIcon(App app)
    {
        _app = app;
        _queue = DispatcherQueue.GetForCurrentThread();
        AllowDarkMenus();
        _instance = GetModuleHandle(null);
        _className = $"ClaudeSyncBarTray_{Environment.ProcessId}_{Guid.NewGuid():N}";
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(), WindowProcedure = Procedure, Instance = _instance, ClassName = _className,
        };
        if (RegisterClassEx(ref windowClass) == 0) throw new InvalidOperationException("알림 영역 창 클래스를 등록하지 못했어요.");
        _self = GCHandle.Alloc(this);
        // A hidden top-level window: it hears the taskbar being recreated and the PC waking.
        _window = CreateWindowEx(0, _className, "Claude SyncBar Tray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            _self.Free();
            UnregisterClass(_className, _instance);
            throw new InvalidOperationException("알림 영역 창을 만들지 못했어요.");
        }

        SetWindowLongPtr(_window, -21, GCHandle.ToIntPtr(_self));
        foreach (var (tone, file) in new[] { (Tone.Healthy, "Tray.ico"), (Tone.Warning, "TrayWarning.ico"), (Tone.Critical, "TrayCritical.ico") })
            _icons[tone] = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", file), ImageIcon,
                GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon), LrLoadFromFile);
        _icon = _icons[Tone.Healthy];
        Add();
        _app.State.Changed += Update;
        Update();
    }

    private void Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        Shell_NotifyIcon(NimAdd, ref data);
        // Version 4 sends keyboard selection and the anchor point of the icon.
        data.TimeoutOrVersion = NotifyIconVersion4;
        Shell_NotifyIcon(NimSetVersion, ref data);
    }

    // The icon shows the lowest quota of the account in use; the tooltip says it in words.
    private void Update()
    {
        if (_disposed) return;
        var state = _app.State;
        var active = state.Active;
        var tone = Tone.Healthy;
        string tip;
        if (active is null)
        {
            tip = "Claude SyncBar\n적용된 계정 없음";
        }
        else
        {
            var usage = QuotaFormat.Account(active, state.VisibleQuotas, DateTimeOffset.UtcNow, unavailable: state.ConnectionError is not null);
            var values = string.Join(" · ", usage.Quotas.Select(quota => $"{quota.Title} {(quota.Tone == Tone.Muted ? "—" : quota.Value)}"));
            tip = $"Claude SyncBar\n{AppState.AccountName(active)}" + (values.Length > 0 ? " · " + values : "");
            if (usage.Quotas.Any(quota => quota.Tone == Tone.Critical)) tone = Tone.Critical;
            else if (usage.Quotas.Any(quota => quota.Tone == Tone.Warning)) tone = Tone.Warning;
            if (usage.Quotas.Where(quota => quota.Tone is Tone.Critical or Tone.Warning).OrderBy(quota => quota.Remaining).FirstOrDefault() is { } low)
                tip += $"\n{low.Title} 한도 {(low.Tone == Tone.Critical ? "거의 없음" : "얼마 안 남음")} · {low.Short}";
            else if (usage.Status.Length > 0) tip += "\n" + usage.Status;
        }

        if (state.Busy) tip += "\n" + (AppState.Str(state.Operation?["message"]) ?? state.BusyLabel);
        tip = tip.Length > 127 ? tip[..127] : tip;
        var icon = _icons.GetValueOrDefault(tone) is var handle && handle != IntPtr.Zero ? handle : _icons[Tone.Healthy];
        if (tip == _tip && icon == _icon) return;
        _tip = tip;
        _icon = icon;
        var data = Data(NifIcon | NifTip | NifShowTip);
        Shell_NotifyIcon(NimModify, ref data);
    }

    private NotifyIconData Data(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), WindowHandle = _window, Identifier = 1, Flags = flags,
        CallbackMessage = WmTray, Icon = _icon, Tip = _tip, Info = "", InfoTitle = "",
    };

    private TrayFlyout Flyout() => _flyout ??= new TrayFlyout(_app);

    /// <summary>Opens the quick view at the bottom-right corner and returns what to capture. Demo captures only.</summary>
    public UIElement ShowFlyoutForCapture()
    {
        var flyout = Flyout();
        flyout.ShowNear(null);
        return flyout.Surface;
    }

    public void ShowQuickView(PointInt32? anchor) => _queue.TryEnqueue(() => { if (!_disposed) Flyout().Toggle(anchor); });

    /// <summary>The tray menu; from the taskbar readout it can also take the readout away.</summary>
    public void ShowMenu(PointInt32 anchor, bool taskbar = false)
    {
        _queue.TryEnqueue(() =>
        {
            if (_disposed) return;
            _flyout?.Hide();
            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            var busy = _app.State.Busy || _app.State.RefreshRequested;
            AppendMenu(menu, MfString, (uint)Command.Open, "열기");
            AppendMenu(menu, MfString, (uint)Command.QuickView, "빠른 보기\t클릭");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString | (busy ? MfGrayed : 0), (uint)Command.Refresh, "새로고침");
            AppendMenu(menu, MfString | (busy || _app.State.Accounts.Count < 2 ? MfGrayed : 0), (uint)Command.SmartSwitch, "스마트 전환");
            AppendMenu(menu, MfString, (uint)Command.Settings, "설정");
            if (taskbar) AppendMenu(menu, MfString | (_app.State.Error is null ? 0 : MfGrayed), (uint)Command.HideTaskbar, "작업 표시줄에서 숨기기");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, (uint)Command.Quit, "완전히 종료");
            SetMenuDefaultItem(menu, (uint)Command.Open, 0);
            // The menu closes when the user clicks elsewhere only if this window is in front.
            SetForegroundWindow(_window);
            var command = (Command)TrackPopupMenu(menu, TpmReturnCmd | TpmRightButton | TpmBottomAlign, anchor.X, anchor.Y, 0, _window, IntPtr.Zero);
            DestroyMenu(menu);
            PostMessage(_window, WmNull, IntPtr.Zero, IntPtr.Zero);
            switch (command)
            {
                case Command.Open: _app.ShowMainWindow(); break;
                case Command.QuickView: Flyout().ShowNear(anchor); break;
                case Command.Refresh: _ = _app.State.RefreshAsync(); break;
                case Command.SmartSwitch: _ = SmartSwitchAsync(); break;
                case Command.Settings: _app.ShowPage("settings"); break;
                case Command.Quit: _app.Quit(); break;
                case Command.HideTaskbar:
                    _ = _app.State.CommandAsync("settings.save", new JsonObject { ["taskbarUsage"] = false },
                        "작업 표시줄에서 사용량을 숨겼어요. 설정 › 표시에서 다시 켤 수 있어요.");
                    break;
            }
        });
    }

    /// <summary>The smart switch asks in the main window, where there is room to show the target.</summary>
    public async Task SmartSwitchAsync()
    {
        _flyout?.Hide();
        _app.ShowMainWindow();
        await Task.Delay(150);
        if (_app.Window?.RootElement.XamlRoot is { } root) await Flows.SmartSwitchAsync(root);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _app.State.Changed -= Update;
        _flyout?.Dispose();
        var data = Data(0);
        Shell_NotifyIcon(NimDelete, ref data);
        DestroyWindow(_window);
        UnregisterClass(_className, _instance);
        _self.Free();
        foreach (var icon in _icons.Values.Where(icon => icon != IntPtr.Zero)) DestroyIcon(icon);
    }

    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmDestroy) return IntPtr.Zero;
        var tray = Find(hwnd);
        if (tray is { _disposed: false })
        {
            if (message == WmTaskbarCreated && WmTaskbarCreated != 0)
            {
                tray.Add();
                return IntPtr.Zero;
            }

            if (message == WmTray)
            {
                // Version 4: the event is in the low word, the icon's anchor in wParam.
                var notification = (uint)(lParam.ToInt64() & 0xFFFF);
                var anchor = new PointInt32((short)(wParam.ToInt64() & 0xFFFF), (short)((wParam.ToInt64() >> 16) & 0xFFFF));
                if (notification is NinSelect or NinKeySelect) tray.ShowQuickView(anchor);
                else if (notification == WmContextMenu) tray.ShowMenu(anchor);
                return IntPtr.Zero;
            }

            if (message == WmPowerBroadcast && (wParam == PbtResumeSuspend || wParam == PbtResumeAutomatic))
                tray._queue.TryEnqueue(() => _ = tray._app.State.RefreshAsync());
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static TrayIcon? Find(IntPtr hwnd)
    {
        var handle = GetWindowLongPtr(hwnd, -21);
        if (handle == IntPtr.Zero) return null;
        try { return GCHandle.FromIntPtr(handle).Target as TrayIcon; }
        catch (InvalidOperationException) { return null; }
    }

    // Lets the menu follow the dark theme, as Windows' own tray menus do.
    private static void AllowDarkMenus()
    {
        try
        {
            SetPreferredAppMode(1);
            FlushMenuThemes();
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException) { }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public WndProcDelegate WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Identifier;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint identifier, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr owner, IntPtr rectangle);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint imageType, int width, int height, uint loadFlags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();
}
