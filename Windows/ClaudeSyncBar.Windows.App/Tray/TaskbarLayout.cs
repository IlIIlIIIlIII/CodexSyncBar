using System.Runtime.InteropServices;
using Microsoft.Win32;
using static ClaudeSyncBar.App.Tray.TaskbarNative;

namespace ClaudeSyncBar.App.Tray;

/// <summary>Where the readout may go on the taskbar, in the taskbar's own pixels.</summary>
/// <param name="FromLeft">Whether <paramref name="Edge"/> is the readout's left edge (else its right edge).</param>
/// <param name="Limit">How far the readout may reach on the other side; null when the taskbar's buttons are unknown.</param>
internal sealed record TaskbarPlace(IntPtr Taskbar, uint Dpi, bool Light, int Height, bool FromLeft, int Edge, int? Limit)
{
    public double Scale => Dpi / 96.0;

    /// <summary>The readout's left edge for a readout this wide, or null when it does not fit.</summary>
    public int? Left(int width)
    {
        var x = FromLeft ? Edge : Edge - width;
        if (x < 0) return null;
        if (Limit is { } limit && (FromLeft ? x + width > limit : x < limit)) return null;
        return x;
    }
}

/// <summary>
/// Finds the readout's place on the primary taskbar. A centred taskbar has it at
/// the far left (after the Widgets button when that is there) as far as the
/// Start button; a left-aligned one just left of the notification area. The
/// buttons come from UI Automation; without it a centred taskbar is still
/// used, minus the room check, unless the Widgets button may be in the way.
/// Never call it on the readout's own thread: UI Automation asks its window too.
/// </summary>
internal static class TaskbarLayout
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int AutomationIdProperty = 30011, ClassNameProperty = 30012, BoundingRectangleProperty = 30001;
    private const int Descendants = 4;
    private static IUIAutomation? _automation;

    public static TaskbarPlace? Find()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var frame) || frame.Width <= 0 || frame.Height <= 0) return null;
        var dpi = GetDpiForWindow(taskbar);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96.0;
        int Dip(double value) => (int)Math.Round(value * scale);
        int Local(int x) => x - frame.Left;
        var light = Number(Personalize, "SystemUsesLightTheme") == 1;
        // Both are on unless the user turned them off.
        var centred = Number(Advanced, "TaskbarAl") != 0;
        var widgetsOn = Number(Advanced, "TaskbarDa") != 0;
        var buttons = Buttons(taskbar, tasks: !centred);

        if (centred)
        {
            if (buttons is null) return widgetsOn ? null : new(taskbar, dpi, light, frame.Height, true, Dip(12), null);
            var edge = buttons.Widgets is { } widgets && Local(widgets.Left) < frame.Width / 2 ? Local(widgets.Right) + Dip(4) : Dip(12);
            return new(taskbar, dpi, light, frame.Height, true, edge, buttons.Start is { } start ? Local(start.Left) - Dip(8) : null);
        }

        var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out var area)) return null;
        var right = Local(area.Left) - Dip(6);
        if (buttons?.Widgets is { } widget && Local(widget.Left) >= frame.Width / 2 && Local(widget.Left) < right) right = Local(widget.Left) - Dip(4);
        int? left = buttons?.LastTask is { } last ? Local(last.Right) + Dip(8) : buttons?.Start is { } first ? Local(first.Right) + Dip(8) : null;
        return new(taskbar, dpi, light, frame.Height, false, right, left);
    }

    private static int? Number(string key, string name)
    {
        try
        {
            using var opened = Registry.CurrentUser.OpenSubKey(key);
            return opened?.GetValue(name) is int value ? value : null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return null; }
    }

    private sealed record Found(Rect? Start, Rect? Widgets, Rect? LastTask);

    // The Start button, the Widgets button and the last task button, in screen pixels; null when UI Automation cannot tell.
    private static Found? Buttons(IntPtr taskbar, bool tasks)
    {
        try
        {
            var automation = _automation ??= (IUIAutomation)new CUIAutomation();
            if (automation.ElementFromHandle(taskbar, out var root) != 0 || root is null) return null;
            Rect? First(int property, string value)
            {
                if (automation.CreatePropertyCondition(property, value, out var condition) != 0 || condition is null) return null;
                return root.FindFirst(Descendants, condition, out var element) == 0 && element is not null ? Bounds(element) : null;
            }

            var start = First(AutomationIdProperty, "StartButton");
            // Every Windows 11 taskbar has a Start button; without it the tree is not the one this reads.
            if (start is null) return null;
            Rect? lastTask = null;
            if (tasks && automation.CreatePropertyCondition(ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer", out var taskCondition) == 0
                && taskCondition is not null && root.FindAll(Descendants, taskCondition, out var found) == 0 && found is not null
                && found.GetLength(out var count) == 0)
            {
                for (var i = 0; i < count; i++)
                    if (found.GetElement(i, out var element) == 0 && element is not null && Bounds(element) is { } bounds
                        && (lastTask is null || bounds.Right > lastTask.Value.Right))
                        lastTask = bounds;
            }

            return new(start, First(AutomationIdProperty, "WidgetsButton"), lastTask);
        }
        catch (Exception error) when (error is COMException or InvalidCastException or InvalidComObjectException or ArgumentException)
        {
            _automation = null;
            return null;
        }
    }

    private static Rect? Bounds(IUIAutomationElement element)
    {
        if (element.GetCurrentPropertyValue(BoundingRectangleProperty, out var value) != 0 || value is not double[] { Length: 4 } box || box[2] <= 0) return null;
        return new Rect
        {
            Left = (int)Math.Round(box[0]), Top = (int)Math.Round(box[1]),
            Right = (int)Math.Round(box[0] + box[2]), Bottom = (int)Math.Round(box[1] + box[3]),
        };
    }

    // UI Automation's client interfaces, declared up to the last method used; the rest keep their slots.
    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IUIAutomationElement? element);
        void ElementFromPoint();
        void GetFocusedElement();
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        void GetControlViewWalker();
        void GetContentViewWalker();
        void GetRawViewWalker();
        void GetRawViewCondition();
        void GetControlViewCondition();
        void GetContentViewCondition();
        void CreateCacheRequest();
        void CreateTrueCondition();
        void CreateFalseCondition();
        [PreserveSig] int CreatePropertyCondition(int property, [MarshalAs(UnmanagedType.Struct)] object value, out IUIAutomationCondition? condition);
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        [PreserveSig] int FindFirst(int scope, IUIAutomationCondition condition, out IUIAutomationElement? found);
        [PreserveSig] int FindAll(int scope, IUIAutomationCondition condition, out IUIAutomationElementArray? found);
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [PreserveSig] int GetCurrentPropertyValue(int property, [MarshalAs(UnmanagedType.Struct)] out object? value);
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        [PreserveSig] int GetLength(out int length);
        [PreserveSig] int GetElement(int index, out IUIAutomationElement? element);
    }
}
