using System.Runtime.InteropServices.WindowsRuntime;
using ClaudeSyncBar.App.Model;
using ClaudeSyncBar.App.Tray;
using ClaudeSyncBar.Ubuntu.Backend;
using ClaudeSyncBar.Windows.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ClaudeSyncBar.App;

public partial class App : Application
{
    public static new App? Current { get; private set; }
    public LaunchOptions Options { get; }
    public AppState State { get; }
    public MainWindow? Window { get; private set; }
    public TrayIcon? Tray => _tray;
    private readonly IManagementService _service;
    private TrayIcon? _tray;
    private TaskbarBand? _taskbar;
    private DispatcherQueueTimer? _poll;
    private bool _notificationsRegistered;

    public App(LaunchOptions options)
    {
        Current = this;
        Options = options;
        InitializeComponent();
        // A capture or demo never reads or changes the real accounts.
        _service = options.Isolated ? DemoData.Create() : new ManagementService(new WindowsPaths());
        BackendPlatform.LaunchCommand = Environment.ProcessPath;
        State = new AppState(new ServiceClient(_service));
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        BackendPlatform.Notifier = message => queue.TryEnqueue(() => Notify(message));
        Window = new MainWindow(this);
        if (!Options.Isolated) RegisterNotifications();
        if (Options.CapturePath is null || Options.CaptureTray) _tray = new TrayIcon(this);
        if (Options.CapturePath is null) _taskbar = CreateTaskbar();
        _poll = queue.CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(2);
        _poll.Tick += (_, _) => _ = State.PollAsync();
        _poll.Start();
        _ = State.PollAsync();
        if (Options.CaptureTray) _ = CaptureTrayAsync();
        else if (Options.CaptureTaskbar && Options.CapturePath is not null) _ = CaptureTaskbarAsync();
        else if (!Options.Background) Window.Activate();
        if (Options.CapturePath is not null && !Options.CaptureTray && !Options.CaptureTaskbar) _ = CaptureAsync();
    }

    // The readout is an extra; the app runs on without it if the taskbar cannot take it.
    private TaskbarBand? CreateTaskbar()
    {
        try { return new TaskbarBand(this); }
        catch (Exception) { return null; }
    }

    public void ShowMainWindow() => Window?.DispatcherQueue.TryEnqueue(() => Window.ShowAndActivate());

    public void ShowPage(string tag)
    {
        ShowMainWindow();
        Window?.DispatcherQueue.TryEnqueue(() => Window.Navigate(tag));
    }

    /// <summary>Ends the service and the app; the window closing alone never does.</summary>
    public async void Quit()
    {
        _poll?.Stop();
        try { await State.Client.CallAsync("quit", timeout: TimeSpan.FromSeconds(120)); } catch (ServiceError) { }
        try { await _service.StopAsync().WaitAsync(TimeSpan.FromSeconds(120)); } catch (Exception) { }
        _taskbar?.Dispose();
        _tray?.Dispose();
        if (_notificationsRegistered) AppNotificationManager.Default.Unregister();
        Window?.CloseForExit();
        Exit();
    }

    private void RegisterNotifications()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, _) => ShowMainWindow();
            AppNotificationManager.Default.Register();
            _notificationsRegistered = true;
        }
        catch (Exception) { /* Notifications are optional; results are shown in the app. */ }
    }

    private void Notify(string message)
    {
        State.Say(message);
        if (!_notificationsRegistered) return;
        try
        {
            AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText("Claude SyncBar").AddText(message).BuildNotification());
        }
        catch (Exception) { }
    }

    // Renders the window to a PNG for design review, then quits. Only ever with demo data.
    private async Task CaptureAsync()
    {
        await Task.Delay(2500);
        await SavePngAsync(Window!.RootElement, Options.CapturePath!);
        Window.CloseForExit();
        Exit();
    }

    private async Task CaptureTrayAsync()
    {
        await Task.Delay(1500);
        var flyout = _tray!.ShowFlyoutForCapture();
        await Task.Delay(1500);
        await SavePngAsync(flyout, Options.CapturePath!);
        Exit();
    }

    // The taskbar readout and its tooltip, drawn as on a taskbar of the chosen theme at this display's scale.
    private async Task CaptureTaskbarAsync()
    {
        await Task.Delay(1500);
        var usage = TaskbarUsage.Of(State.Settings, State.Accounts, State.ActiveProfileId, DateTimeOffset.UtcNow, State.ConnectionError is not null);
        if (usage is not null)
        {
            var taskbar = TaskbarNative.FindWindow("Shell_TrayWnd", null);
            var dpi = taskbar == IntPtr.Zero ? 0 : TaskbarNative.GetDpiForWindow(taskbar);
            var (pixels, width, height) = TaskbarBand.Preview(usage, (dpi == 0 ? 96 : dpi) / 96.0, Options.Theme == "light");
            await SavePixelsAsync(pixels, width, height, Options.CapturePath!);
        }

        Window?.CloseForExit();
        Exit();
    }

    internal static async Task SavePngAsync(UIElement element, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync();
        await SavePixelsAsync(pixels.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight, path);
    }

    private static async Task SavePixelsAsync(byte[] pixels, int width, int height, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
    }
}
