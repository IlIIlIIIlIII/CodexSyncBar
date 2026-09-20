using CodexSyncBar.Windows.Core;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace CodexSyncBar_Windows;

public partial class App : Application
{
    private AppInstance? _primaryInstance;
    private TrayIconService? _trayIcon;
    private DashboardPipeServer? _pipeServer;
    private bool _isDemo;
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;
    private int _pendingForegroundRequest;

    public MainWindow? MainWindow { get; private set; }
    public TrayIconService? TrayIcon => _trayIcon;
    public SyncBarController? Controller { get; private set; }
    public DemoDashboardRuntime? Demo { get; private set; }

    public App() => InitializeComponent();

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            var options = WindowsLaunchOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
            if (AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask)
                options = options with { Background = true };
            if (!options.Background)
                Interlocked.Exchange(ref _pendingForegroundRequest, 1);
            _isDemo = options.UsesReadmePage;
            // Demo runs independently of a live account controller and its pipe.
            _primaryInstance = AppInstance.FindOrRegisterForKey(_isDemo ? "CodexSyncBar.Demo" : "CodexSyncBar");
            if (!_primaryInstance.IsCurrent)
            {
                await _primaryInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
                Exit();
                return;
            }
            _primaryInstance.Activated += PrimaryInstance_Activated;

            DashboardPipeHandlers handlers;
            if (_isDemo)
            {
                Demo = new DemoDashboardRuntime();
                handlers = Demo.CreateHandlers(OpenSettingsAsync);
            }
            else
            {
                Controller = new SyncBarController(new WindowsPaths());
                await Controller.InitializeAsync();
                handlers = new DashboardPipeHandlers
                {
                    GetSnapshotAsync = Controller.GetSnapshotAsync,
                    RefreshUsageAsync = Controller.RefreshUsageAsync,
                    SwitchAccountAsync = Controller.SwitchAccountAsync,
                    GetOperationStatusAsync = Controller.GetOperationStatusAsync,
                    OpenSettingsAsync = OpenSettingsAsync,
                };
            }
            // Host starts before any page lifecycle event, including --background.
            _pipeServer = new DashboardPipeServer(handlers,
                _isDemo ? DashboardPipeServer.DefaultPipeName + ".demo" : null);
            _pipeServer.Start();
            MainWindow = new MainWindow(options);
            if (MainWindow.Page is { } page)
                _trayIcon = new TrayIconService(MainWindow, new MainPageTrayDashboard(page));
            else if (Demo is not null)
                _trayIcon = new TrayIconService(MainWindow, new DemoTrayDashboard(Demo));
            MainWindow.Closed += MainWindow_Closed;

            // Initial visibility is resolved on the UI thread before page loading.
            // A redirected foreground launch may already be waiting for this window.
            MainWindow.AppWindow.Hide();
            ShowPendingForegroundRequest();

            if (MainWindow.Page is { } mainPage)
                await mainPage.InitializeAsync();

            if (!options.Background && options.LoginProfileId is { } profileId && MainWindow.Page is { } loginPage)
                await loginPage.BeginLoginForProfileAsync(profileId);

            if (options.ReadmeOutput is { } output && MainWindow.DemoPage is { } demoPage)
            {
                if (options.ReadmeScreen == WindowsReadmeScreen.Tray && _trayIcon is not null)
                    await _trayIcon.CaptureQuickViewAsync(output);
                else
                    await demoPage.CapturePngAsync(output);
                MainWindow.ExitApplication();
            }
        }
        catch (Exception error)
        {
            RecordStartupFailure(error);
            Console.Error.WriteLine($"Codex SyncBar startup failed: {error.GetType().Name} (0x{error.HResult:X8})");
            Exit();
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _trayIcon?.Dispose();
        if (MainWindow?.Page is { } page)
            await page.ShutdownAsync();
        if (_pipeServer is not null)
            await _pipeServer.DisposeAsync();
        Controller?.Dispose();
        _primaryInstance?.UnregisterKey();
    }

    private Task OpenSettingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Activation and pipe callbacks can arrive while InitializeAsync is awaiting
        // recovery, before MainWindow exists. Keep the request until a UI window can
        // consume it, instead of dropping it through a null-conditional dispatch.
        Interlocked.Exchange(ref _pendingForegroundRequest, 1);
        if (_uiDispatcher?.HasThreadAccess == true)
            ShowPendingForegroundRequest();
        else
            _uiDispatcher?.TryEnqueue(ShowPendingForegroundRequest);
        return Task.CompletedTask;
    }

    private void ShowPendingForegroundRequest()
    {
        if (MainWindow is not { } window
            || Interlocked.Exchange(ref _pendingForegroundRequest, 0) == 0)
            return;

        window.ShowManagementWindow();
    }

    private void PrimaryInstance_Activated(object? sender, AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.StartupTask) return;
        var launchArguments = args.Data is ILaunchActivatedEventArgs launch ? launch.Arguments : string.Empty;
        var isBackground = launchArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("--background");
        if (isBackground) return;
        _ = OpenSettingsAsync(CancellationToken.None);
    }

    private static void RecordStartupFailure(Exception error)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexSyncBar", "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "startup.log"), $"{DateTimeOffset.UtcNow:O} {error.GetType().Name} HRESULT=0x{error.HResult:X8}{Environment.NewLine}");
        }
        catch { /* The original startup exception remains available on stderr. */ }
    }
}
