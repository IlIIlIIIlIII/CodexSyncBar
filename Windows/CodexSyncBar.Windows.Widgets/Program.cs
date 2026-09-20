using System.Runtime.InteropServices;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Widgets;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        WidgetProvider.DemoMode = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        uint registration = 0;
        try
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 0));
            WinRT.ComWrappersSupport.InitializeComWrappers();
            var factory = new WidgetProviderFactory();
            Marshal.ThrowExceptionForHR(CoRegisterClassObject(
                Guid.Parse(WidgetTemplates.ProviderClassId), factory, 4, 1, out registration));
            WidgetProvider.Shutdown.WaitOne();
            GC.KeepAlive(factory);
            return 0;
        }
        catch (Exception ex)
        {
            ProviderLog.Failure("provider-start", ex);
            return 1;
        }
        finally
        {
            if (registration != 0) CoRevokeClassObject(registration);
            CoUninitialize();
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrencyModel);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        [MarshalAs(UnmanagedType.LPStruct)] Guid classId,
        [MarshalAs(UnmanagedType.IUnknown)] object factory,
        uint context, uint flags, out uint registration);
    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint registration);
}

internal static class ProviderLog
{
    // Method names identify initialization failures without logging exception
    // messages, argument values, source paths, request data or credentials.
    private static string SafeFrames(Exception exception) => string.Join(" <- ",
        new System.Diagnostics.StackTrace(exception, true).GetFrames()
            .Take(10).Select(frame => frame.GetMethod())
            .Select(method => $"{method?.DeclaringType?.FullName}.{method?.Name}"));

    public static void Status(string operation, int count = 0)
    {
        Write($"{operation}: count={count}");
    }

    private static void Write(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexSyncBar", "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "widgets.log"), $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Failure(string operation, Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexSyncBar", "Logs");
            Directory.CreateDirectory(directory);
            // No raw exception messages, accounts or request data in logs.
            File.AppendAllText(Path.Combine(directory, "widgets.log"),
                $"{DateTimeOffset.UtcNow:O} {operation}: {exception.GetType().Name} HRESULT=0x{exception.HResult:X8} frames={SafeFrames(exception)}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
