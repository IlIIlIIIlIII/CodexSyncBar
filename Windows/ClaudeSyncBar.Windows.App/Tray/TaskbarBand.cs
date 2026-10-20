using System.Runtime.InteropServices;
using ClaudeSyncBar.App.Model;
using Microsoft.UI.Dispatching;
using Windows.Graphics;
using static ClaudeSyncBar.App.Tray.TaskbarNative;

namespace ClaudeSyncBar.App.Tray;

/// <summary>
/// The usage readout on the taskbar: "5시간 62% │ 전체 주간 45%" at its far left.
/// Windows 11 has no API for taskbar content, so it is a small layered window
/// made a child of the taskbar: it moves with it and hides with auto-hide and
/// full-screen apps. A child of another process's window shares that window's
/// input queue, so it lives on a thread of its own that only draws; a click
/// opens the quick view and the right button the tray menu, in the app.
/// </summary>
public sealed class TaskbarBand : IDisposable
{
    private const uint WmUpdate = WmApp + 1, WmStop = WmApp + 2;
    private static readonly WndProc ControlProcedure = ControlWindow, BandProcedure = BandWindow, TipProcedure = TipWindow;

    private readonly App _app;
    private readonly DispatcherQueue _queue;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Timer _locator;
    private readonly string _controlClass, _bandClass, _tipClass;
    private GCHandle _self;
    private IntPtr _instance, _control, _band, _tip;
    private volatile TaskbarUsage? _usage;
    private volatile TaskbarPlace? _place;
    private TaskbarUsage? _tipUsage;
    private TaskbarPlace? _tipPlace;
    private string? _drawn;
    private bool _hover;
    private int _locating;
    private volatile bool _disposed;

    public TaskbarBand(App app)
    {
        _app = app;
        _queue = DispatcherQueue.GetForCurrentThread();
        var suffix = $"{Environment.ProcessId}_{Guid.NewGuid():N}";
        (_controlClass, _bandClass, _tipClass) = ($"ClaudeSyncBarTaskbarControl_{suffix}", $"ClaudeSyncBarTaskbar_{suffix}", $"ClaudeSyncBarTaskbarTip_{suffix}");
        _self = GCHandle.Alloc(this);
        _thread = new Thread(Run) { IsBackground = true, Name = "Claude SyncBar taskbar" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
        _app.State.Changed += Update;
        Update();
        // The taskbar's buttons move as apps open and close; its place is looked up again every two seconds.
        _locator = new Timer(_ => Locate(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    // On the UI thread: what to show, from the latest snapshot.
    private void Update()
    {
        if (_disposed || _control == IntPtr.Zero) return;
        var state = _app.State;
        var shown = _usage is not null;
        _usage = state.Settings is null && state.ConnectionError is null ? null
            : TaskbarUsage.Of(state.Settings, state.Accounts, state.ActiveProfileId, DateTimeOffset.UtcNow, state.ConnectionError is not null);
        // The first snapshot, or the readout turned back on, finds its place at once rather than at the next lookup.
        if (!shown && _usage is not null) ThreadPool.QueueUserWorkItem(_ => Locate());
        PostMessage(_control, WmUpdate, IntPtr.Zero, IntPtr.Zero);
    }

    // On the thread pool: UI Automation asks the readout's own window too, so never on its thread.
    private void Locate()
    {
        if (_disposed || Interlocked.Exchange(ref _locating, 1) == 1) return;
        try
        {
            if (_usage is null) return;
            _place = TaskbarLayout.Find();
            PostMessage(_control, WmUpdate, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception)
        {
            _place = null;
        }
        finally
        {
            Volatile.Write(ref _locating, 0);
        }
    }

    private void Run()
    {
        _instance = GetModuleHandle(null);
        Register(_controlClass, ControlProcedure, 0);
        Register(_bandClass, BandProcedure, 0);
        Register(_tipClass, TipProcedure, CsDropShadow);
        _control = CreateWindowEx(0, _controlClass, "", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, _instance, IntPtr.Zero);
        SetWindowLongPtr(_control, GwlpUserData, GCHandle.ToIntPtr(_self));
        _ready.Set();
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        if (_tip != IntPtr.Zero) DestroyWindow(_tip);
        if (_band != IntPtr.Zero && IsWindow(_band)) DestroyWindow(_band);
        DestroyWindow(_control);
        foreach (var name in new[] { _controlClass, _bandClass, _tipClass }) UnregisterClass(name, _instance);
    }

    private void Register(string name, WndProc procedure, uint style)
    {
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(), Style = style, WindowProcedure = procedure, Instance = _instance,
            ClassName = name, Cursor = LoadCursor(IntPtr.Zero, IdcArrow),
        };
        RegisterClassEx(ref windowClass);
    }

    // On the readout's thread: draws it again when anything shown changed, and keeps it above the taskbar's own content.
    private void Refresh()
    {
        if (_usage is not { } usage || _place is not { } place)
        {
            Hide();
            return;
        }

        if (_band == IntPtr.Zero || !IsWindow(_band) || GetParent(_band) != place.Taskbar)
        {
            if (_band != IntPtr.Zero && IsWindow(_band)) DestroyWindow(_band);
            _band = CreateWindowEx(WsExLayered, _bandClass, "Claude SyncBar 사용량", WsChild | WsClipSiblings, 0, 0, 1, 1,
                place.Taskbar, IntPtr.Zero, _instance, IntPtr.Zero);
            _drawn = null;
            _hover = false;
            if (_band == IntPtr.Zero) return;
            SetWindowLongPtr(_band, GwlpUserData, GCHandle.ToIntPtr(_self));
        }

        var key = string.Join("|", usage.Parts.Select(part => $"{part.Label}:{part.Value}:{part.Tone}"))
            + $"|{place.Dpi}|{place.Light}|{place.Height}|{place.Edge}|{place.Limit}|{place.FromLeft}|{_hover}";
        if (key == _drawn && IsWindowVisible(_band))
        {
            SetWindowPos(_band, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            return;
        }

        using var frame = TaskbarPainter.Readout(usage, place.Scale, place.Light, _hover);
        if (place.Left(frame.Width) is not { } x)
        {
            Hide();
            return;
        }

        var size = new Size { Cx = frame.Width, Cy = frame.Height };
        var source = new Point();
        var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 };
        UpdateLayeredWindow(_band, IntPtr.Zero, IntPtr.Zero, ref size, frame.Dc, ref source, 0, ref blend, UlwAlpha);
        SetWindowPos(_band, HwndTop, x, (place.Height - frame.Height) / 2, frame.Width, frame.Height, SwpNoActivate | SwpShowWindow);
        SetWindowText(_band, usage.Spoken);
        _drawn = key;
        if (_tipUsage is not null) ShowTip();
    }

    private void Hide()
    {
        HideTip();
        if (_band != IntPtr.Zero && IsWindow(_band)) ShowWindow(_band, SwHide);
        _drawn = null;
    }

    private void ShowTip()
    {
        if (_usage is not { } usage || _place is not { } place || _band == IntPtr.Zero || !IsWindowVisible(_band)) return;
        if (_tip == IntPtr.Zero)
        {
            _tip = CreateWindowEx(WsExToolWindow | WsExTopmost | WsExNoActivate | WsExTransparent, _tipClass, "", WsPopup, 0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
            if (_tip == IntPtr.Zero) return;
            SetWindowLongPtr(_tip, GwlpUserData, GCHandle.ToIntPtr(_self));
            var corner = DwmwcpRoundSmall;
            DwmSetWindowAttribute(_tip, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        }

        var measure = CreateCompatibleDC(IntPtr.Zero);
        var (width, height) = TaskbarPainter.Tip(measure, usage, place.Scale, place.Light, draw: false);
        DeleteDC(measure);
        GetWindowRect(_band, out var band);
        GetWindowRect(place.Taskbar, out var taskbar);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromWindow(_band, MonitorDefaultToNearest), ref info);
        var gap = (int)Math.Round(8 * place.Scale);
        var x = Math.Clamp(band.Left, info.Work.Left + gap, Math.Max(info.Work.Left + gap, info.Work.Right - width - gap));
        // Above the taskbar, or below it when the taskbar is at the top.
        var y = taskbar.Top - gap - height >= info.Monitor.Top ? taskbar.Top - gap - height : taskbar.Bottom + gap;
        _tipUsage = usage;
        _tipPlace = place;
        SetWindowPos(_tip, HwndTopmost, x, y, width, height, SwpNoActivate | SwpShowWindow);
        InvalidateRect(_tip, IntPtr.Zero, false);
    }

    private void HideTip()
    {
        _tipUsage = null;
        if (_tip != IntPtr.Zero) ShowWindow(_tip, SwHide);
    }

    // The app's windows open where the readout is: the quick view above it, the menu at the pointer.
    private void Click(bool menu)
    {
        HideTip();
        if (!GetWindowRect(_band, out var rect)) return;
        GetCursorPos(out var cursor);
        var anchor = new PointInt32(menu ? cursor.X : rect.Left + rect.Width / 2, rect.Top);
        _queue.TryEnqueue(() =>
        {
            if (_disposed || _app.Tray is not { } tray) return;
            if (menu) tray.ShowMenu(anchor, taskbar: true);
            else tray.ShowQuickView(anchor);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _app.State.Changed -= Update;
        _locator.Dispose();
        PostMessage(_control, WmStop, IntPtr.Zero, IntPtr.Zero);
        if (_thread.Join(TimeSpan.FromSeconds(3))) _self.Free();
    }

    /// <summary>
    /// The readout as it looks on a taskbar, plain and under the pointer with its
    /// tooltip, as BGRA pixels for a design capture. Never touches the real taskbar.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) Preview(TaskbarUsage usage, double scale, bool light)
    {
        int S(double value) => (int)Math.Round(value * scale);
        using var plain = TaskbarPainter.Readout(usage, scale, light, hover: false);
        using var hovered = TaskbarPainter.Readout(usage, scale, light, hover: true);
        var measure = CreateCompatibleDC(IntPtr.Zero);
        var (tipWidth, tipHeight) = TaskbarPainter.Tip(measure, usage, scale, light, draw: false);
        DeleteDC(measure);
        using var tip = new TaskbarFrame(tipWidth, tipHeight);
        TaskbarPainter.Tip(tip.Dc, usage, scale, light, draw: true);

        var width = Math.Max(S(12) + tipWidth, S(12) + hovered.Width) + S(24);
        var bar = S(48);
        var tipTop = S(16);
        var firstBar = tipTop + tipHeight + S(8);
        var secondBar = firstBar + bar + S(16);
        var height = secondBar + bar;
        var pixels = new uint[width * height];
        Array.Fill(pixels, light ? 0xFFC9D3DDu : 0xFF2A3440u);
        foreach (var top in new[] { firstBar, secondBar })
            for (var y = top; y < top + bar; y++)
                Array.Fill(pixels, light ? 0xFFF3F3F3u : 0xFF202020u, y * width, width);

        void Place(TaskbarFrame frame, int left, int top, bool opaque)
        {
            var source = frame.Read();
            for (var y = 0; y < frame.Height; y++)
                for (var x = 0; x < frame.Width; x++)
                {
                    var pixel = source[y * frame.Width + x];
                    ref var target = ref pixels[(top + y) * width + left + x];
                    if (opaque)
                    {
                        target = pixel | 0xFF000000;
                        continue;
                    }

                    var keep = 1 - ((pixel >> 24) & 0xFF) / 255.0;
                    var below = target;
                    uint Channel(int shift) => (uint)Math.Min(255, Math.Round(((pixel >> shift) & 0xFF) + ((below >> shift) & 0xFF) * keep));
                    target = 0xFF000000 | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
                }
        }

        Place(tip, S(12), tipTop, opaque: true);
        Place(hovered, S(12), firstBar + (bar - hovered.Height) / 2, opaque: false);
        Place(plain, S(12), secondBar + (bar - plain.Height) / 2, opaque: false);
        var bytes = new byte[pixels.Length * 4];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        return (bytes, width, height);
    }

    private static TaskbarBand? Find(IntPtr hwnd)
    {
        var handle = GetWindowLongPtr(hwnd, GwlpUserData);
        if (handle == IntPtr.Zero) return null;
        try { return GCHandle.FromIntPtr(handle).Target as TaskbarBand; }
        catch (InvalidOperationException) { return null; }
    }

    private static IntPtr ControlWindow(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (Find(hwnd) is { } band)
        {
            if (message == WmUpdate)
            {
                if (!band._disposed) band.Refresh();
                return IntPtr.Zero;
            }

            if (message == WmStop)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static IntPtr BandWindow(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (Find(hwnd) is { _disposed: false } band)
        {
            switch (message)
            {
                // A click leaves the window in front where it was; the app decides what comes forward.
                case WmMouseActivate:
                    return MaNoActivate;
                case WmMouseMove:
                    if (!band._hover)
                    {
                        band._hover = true;
                        var track = new TrackMouseEventInfo { Size = (uint)Marshal.SizeOf<TrackMouseEventInfo>(), Flags = TmeHover | TmeLeave, Hwnd = hwnd, HoverTime = 400 };
                        TrackMouseEvent(ref track);
                        band.Refresh();
                    }

                    return IntPtr.Zero;
                case WmMouseHover:
                    band.ShowTip();
                    return IntPtr.Zero;
                case WmMouseLeave:
                    band._hover = false;
                    band.HideTip();
                    band.Refresh();
                    return IntPtr.Zero;
                case WmLButtonUp:
                    band.Click(menu: false);
                    return IntPtr.Zero;
                case WmRButtonUp:
                    band.Click(menu: true);
                    return IntPtr.Zero;
                case WmDestroy:
                    // Explorer restarting takes the taskbar and this child with it; the next lookup makes a new one.
                    if (band._band == hwnd)
                    {
                        band._band = IntPtr.Zero;
                        band._drawn = null;
                        band._hover = false;
                        band.HideTip();
                    }

                    return IntPtr.Zero;
            }
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static IntPtr TipWindow(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmPaint && Find(hwnd) is { } band)
        {
            var dc = BeginPaint(hwnd, out var paint);
            if (band._tipUsage is { } usage && band._tipPlace is { } place) TaskbarPainter.Tip(dc, usage, place.Scale, place.Light, draw: true);
            EndPaint(hwnd, ref paint);
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }
}
