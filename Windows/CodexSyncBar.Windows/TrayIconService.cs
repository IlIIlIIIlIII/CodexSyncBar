using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace CodexSyncBar_Windows;

/// <summary>
/// Native Shell_NotifyIcon integration. WinUI 3 does not expose a tray icon
/// control, so keeping this small implementation native avoids bringing WPF or
/// Windows Forms into the app's dependency graph.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint WmApp = 0x8000;
    private const uint WmTray = WmApp + 1;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint WmPowerBroadcast = 0x0218;
    private const nint PbtResumeSuspend = 0x0007;
    private const nint PbtResumeAutomatic = 0x0012;
    private const uint WmNull = 0x0000;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint WmDestroy = 0x0002;
    private const int HwndMessage = -3;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrDefaultSize = 0x00000040;

    private static readonly WndProcDelegate WndProc = WindowProcedure;
    private readonly MainWindow _window;
    private readonly MainPage _page;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Action _showWindow;
    private readonly Func<Task> _refresh;
    private readonly Func<Task> _refreshAll;
    private readonly IntPtr _windowHandle;
    private readonly IntPtr _iconHandle;
    private readonly bool _ownsIcon;
    private readonly ushort _classAtom;
    private readonly string _className;
    private readonly IntPtr _instance;
    private GCHandle _selfHandle;
    private bool _disposed;
    private string _title = "Codex SyncBar";
    private TrayPopoverWindow? _popover;

    public TrayIconService(MainWindow window, MainPage page)
    {
        _window = window;
        _page = page;
        _dispatcherQueue = window.DispatcherQueue;
        _showWindow = ShowWindow;
        _refresh = page.RefreshFromTrayAsync;
        _refreshAll = page.RefreshTrayPopoverAsync;

        var className = $"CodexSyncBarTray_{Environment.ProcessId}_{Guid.NewGuid():N}";
        var instance = GetModuleHandle(null);
        _className = className;
        _instance = instance;
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            Style = 0,
            WindowProcedure = WndProc,
            ClassExtraBytes = 0,
            WindowExtraBytes = 0,
            Instance = instance,
            Icon = IntPtr.Zero,
            SmallIcon = IntPtr.Zero,
            Cursor = IntPtr.Zero,
            Background = IntPtr.Zero,
            MenuName = null,
            ClassName = className,
        };
        _classAtom = RegisterClassEx(ref windowClass);
        if (_classAtom == 0)
        {
            throw new InvalidOperationException("Windows 알림 영역 창 클래스를 등록하지 못했습니다.");
        }

        _selfHandle = GCHandle.Alloc(this);
        _windowHandle = CreateWindowEx(
            0,
            className,
            "Codex SyncBar Tray",
            0,
            0,
            0,
            0,
            0,
            new IntPtr(HwndMessage),
            IntPtr.Zero,
            instance,
            GCHandle.ToIntPtr(_selfHandle));
        if (_windowHandle == IntPtr.Zero)
        {
            _selfHandle.Free();
            UnregisterClass(className, instance);
            throw new InvalidOperationException("Windows 알림 영역 창을 만들지 못했습니다.");
        }
        SetWindowLongPtr(_windowHandle, -21, GCHandle.ToIntPtr(_selfHandle));

        _iconHandle = LoadTrayIcon(out _ownsIcon);
        var data = CreateNotifyData(NifMessage | NifIcon | NifTip);
        if (!Shell_NotifyIcon(NimAdd, ref data))
        {
            DestroyWindow(_windowHandle);
            _selfHandle.Free();
            UnregisterClass(className, instance);
            if (_ownsIcon && _iconHandle != IntPtr.Zero)
            {
                DestroyIcon(_iconHandle);
            }

            throw new InvalidOperationException("Windows 알림 영역 아이콘을 등록하지 못했습니다.");
        }
    }

    public void SetUsageTitle(string title)
    {
        if (_disposed)
        {
            return;
        }

        _title = string.IsNullOrWhiteSpace(title) ? "Codex SyncBar" : title.Trim();
        if (_title.Length > 63)
        {
            _title = _title[..63];
        }

        var data = CreateNotifyData(NifTip);
        Shell_NotifyIcon(NimModify, ref data);
    }

    public string CurrentTitle => _title;

    public void ShowQuickView()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            var displayArea = DisplayArea.GetFromWindowId(
                _window.AppWindow.Id,
                DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;
            var anchor = new PointInt32(
                workArea.X + workArea.Width - 16,
                workArea.Y + workArea.Height + 4);
            EnsurePopover().ShowAt(anchor);
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _popover?.Dispose();
        _popover = null;
        var data = CreateNotifyData(0);
        Shell_NotifyIcon(NimDelete, ref data);
        DestroyWindow(_windowHandle);
        UnregisterClass(_className, _instance);
        _selfHandle.Free();
        if (_ownsIcon && _iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
        }

    }

    private NotifyIconData CreateNotifyData(uint flags)
    {
        return new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _windowHandle,
            Identifier = 1,
            Flags = flags,
            CallbackMessage = WmTray,
            Icon = _iconHandle,
            Tip = _title,
            Info = string.Empty,
            InfoTitle = string.Empty,
        };
    }

    private void ShowWindow()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _popover?.Hide();
            _window.Activate();
            _window.AppWindow.Show();
        });
    }

    private void ToggleQuickView()
    {
        GetCursorPos(out var point);
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            EnsurePopover().ToggleAt(new PointInt32(point.X, point.Y));
        });
    }

    private TrayPopoverWindow EnsurePopover() =>
        _popover ??= new TrayPopoverWindow(_page, _showWindow, _window.ExitApplication);

    private void ShowContextMenu()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                return;
            }

            AppendMenu(menu, MfString, 1, "빠른 보기");
            AppendMenu(menu, MfString, 2, "전체 관리 창 열기");
            AppendMenu(menu, MfString, 3, "사용량·장치 새로고침");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, 4, "종료");
            GetCursorPos(out var point);
            SetForegroundWindow(_windowHandle);
            var command = TrackPopupMenu(
                menu,
                TpmReturnCmd | TpmRightButton,
                point.X,
                point.Y,
                0,
                _windowHandle,
                IntPtr.Zero);
            DestroyMenu(menu);
            PostMessage(_windowHandle, WmNull, IntPtr.Zero, IntPtr.Zero);
            switch (command)
            {
                case 1:
                    ToggleQuickView();
                    break;
                case 2:
                    ShowWindow();
                    break;
                case 3:
                    _ = _refreshAll();
                    break;
                case 4:
                    _window.ExitApplication();
                    break;
            }
        });
    }

    private IntPtr LoadTrayIcon(out bool ownsIcon)
    {
        ownsIcon = false;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        var icon = File.Exists(path)
            ? LoadImage(IntPtr.Zero, path, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize)
            : IntPtr.Zero;
        if (icon != IntPtr.Zero)
        {
            ownsIcon = true;
            return icon;
        }

        return LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmDestroy)
        {
            return IntPtr.Zero;
        }

        if (message == WmTray)
        {
            var service = FindService(hwnd);
            if (service is not null)
            {
                var notification = unchecked((uint)lParam.ToInt64());
                if (notification == WmRButtonUp || notification == WmContextMenu)
                {
                    service.ShowContextMenu();
                }
                else if (notification == WmLButtonUp)
                {
                    service.ToggleQuickView();
                }
            }
        }

        if (message == WmPowerBroadcast
            && (wParam == PbtResumeSuspend || wParam == PbtResumeAutomatic))
        {
            var service = FindService(hwnd);
            service?.RefreshAfterWake();
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void RefreshAfterWake()
    {
        if (_disposed)
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(() => _ = _refresh());
    }

    private static TrayIconService? FindService(IntPtr hwnd)
    {
        var handle = GetWindowLongPtr(hwnd, -21);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return GCHandle.FromIntPtr(handle).Target as TrayIconService;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, index)
            : new IntPtr(GetWindowLong32(hwnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, index, value)
            : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));

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

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint identifier, string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        int reserved,
        IntPtr owner,
        IntPtr rectangle);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(
        IntPtr instance,
        string name,
        uint imageType,
        int width,
        int height,
        uint loadFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
}
