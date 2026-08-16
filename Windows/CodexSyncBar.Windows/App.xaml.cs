using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Xaml;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar_Windows;

public partial class App : Application
{
    private static readonly AppInstance PrimaryInstance =
        AppInstance.FindOrRegisterForKey("CodexSyncBar");

    public MainWindow? MainWindow { get; private set; }

    public TrayIconService? TrayIcon => _trayIcon;

    private TrayIconService? _trayIcon;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        WindowsLaunchOptions launchOptions;
        try
        {
            launchOptions = WindowsLaunchOptions.Parse(
                Environment.GetCommandLineArgs().Skip(1).ToArray());
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            Environment.Exit(1);
            return;
        }

        if (!PrimaryInstance.IsCurrent)
        {
            RedirectToPrimaryInstance();
            return;
        }

        PrimaryInstance.Activated -= PrimaryInstance_Activated;
        PrimaryInstance.Activated += PrimaryInstance_Activated;
        if (MainWindow is not null)
        {
            MainWindow.Activate();
            _ = RunLaunchActionAsync(launchOptions);
            return;
        }

        MainWindow = new MainWindow(launchOptions);
        if (MainWindow.Page is { } page)
        {
            _trayIcon = new TrayIconService(MainWindow, page);
        }
        MainWindow.Closed += (_, _) =>
        {
            try
            {
                MainWindow.Page?.ShutdownAsync().GetAwaiter().GetResult();
            }
            finally
            {
                _trayIcon?.Dispose();
            }
        };
        MainWindow.Activate();
        if (!launchOptions.UsesReadmePage && launchOptions.LoginProfileId is null)
        {
            MainWindow.AppWindow.Hide();
            _trayIcon?.ShowQuickView();
        }
        _ = RunLaunchActionAsync(launchOptions);
    }

    private async Task RunLaunchActionAsync(WindowsLaunchOptions launchOptions)
    {
        try
        {
            if (launchOptions.LoginProfileId is { } profileId)
            {
                if (MainWindow?.Page is not { } page)
                {
                    throw new InvalidOperationException("로그인 실행 화면을 준비하지 못했습니다.");
                }

                await page.BeginLoginForProfileAsync(profileId);
            }

            if (launchOptions.ReadmeOutput is { } outputPath)
            {
                if (MainWindow?.DemoPage is not { } demoPage)
                {
                    throw new InvalidOperationException("README 캡처 화면을 준비하지 못했습니다.");
                }

                await demoPage.CapturePngAsync(outputPath);
                Environment.Exit(0);
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            Environment.Exit(1);
        }
    }

    private static void RedirectToPrimaryInstance()
    {
        try
        {
            var currentInstance = AppInstance.GetCurrent();
            var activationArguments = currentInstance.GetActivatedEventArgs();
            if (activationArguments is not null)
            {
                PrimaryInstance.RedirectActivationToAsync(activationArguments)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
        }
        finally
        {
            AppInstance.GetCurrent().UnregisterKey();
            Environment.Exit(0);
        }
    }

    private void PrimaryInstance_Activated(object? sender, AppActivationArguments args)
    {
        MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_trayIcon is not null)
            {
                MainWindow?.AppWindow.Hide();
                _trayIcon.ShowQuickView();
                return;
            }

            MainWindow?.Activate();
            MainWindow?.AppWindow.Show();
        });
    }
}
