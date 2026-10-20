using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace ClaudeSyncBar.App;

/// <summary>How the app was started.</summary>
public sealed record LaunchOptions(bool Background, bool Demo, string? Page, string? Theme, string? CapturePath, bool CaptureTray, string? Dialog,
    bool CaptureTaskbar = false)
{
    public static LaunchOptions Parse(string[] args)
    {
        string? Option(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        return new(args.Contains("--background"), args.Contains("--demo"), Option("--page"), Option("--theme"),
            Option("--capture"), args.Contains("--tray"), Option("--dialog"), args.Contains("--taskbar"));
    }

    /// <summary>A demo or a capture runs beside the real app and never touches its data.</summary>
    public bool Isolated => Demo || CapturePath is not null;
}

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var options = LaunchOptions.Parse(args);
        // One app per user: a second start brings the running one forward.
        if (!options.Isolated)
        {
            var main = AppInstance.FindOrRegisterForKey("ClaudeSyncBar");
            if (!main.IsCurrent)
            {
                main.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask().Wait();
                return 0;
            }

            main.Activated += (_, _) => App.Current?.ShowMainWindow();
        }

        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(options);
        });
        return 0;
    }
}
