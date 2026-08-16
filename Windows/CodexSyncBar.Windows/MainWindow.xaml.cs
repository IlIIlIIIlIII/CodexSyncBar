using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar_Windows;

public sealed partial class MainWindow : Window
{
    private readonly bool _isSpecialLaunch;

    public MainWindow(WindowsLaunchOptions? launchOptions = null)
    {
        InitializeComponent();
        _isSpecialLaunch = launchOptions?.UsesReadmePage == true;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Closing += AppWindow_Closing;
        Activated += MainWindow_Activated;
        if (_isSpecialLaunch)
        {
            AppWindow.Title = "Codex SyncBar QA";
            RootFrame.Navigate(
                typeof(ReadmeDemoPage),
                launchOptions?.ReadmeScreen ?? WindowsReadmeScreen.Popover);
        }
        else
        {
            RootFrame.Navigate(typeof(MainPage));
        }
    }

    public MainPage? Page => RootFrame.Content as MainPage;

    public ReadmeDemoPage? DemoPage => RootFrame.Content as ReadmeDemoPage;

    public void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    private bool _isExiting;

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (Page is { } page)
        {
            _ = page.RefreshUsageIfStaleAsync();
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isExiting || _isSpecialLaunch)
        {
            return;
        }

        args.Cancel = true;
        AppWindow.Hide();
    }
}
