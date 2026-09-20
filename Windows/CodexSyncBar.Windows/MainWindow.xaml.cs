using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using CodexSyncBar.Windows.Core;
using System.Runtime.InteropServices;

namespace CodexSyncBar_Windows;

public sealed partial class MainWindow : Window
{
    private readonly bool _isSpecialLaunch;
    private readonly bool _isCaptureLaunch;
    private bool _updatingWindowBounds;
    private (int MinWidth, int MinHeight, int MaxWidth, int MaxHeight)? _windowBounds;
    private XamlRoot? _sizingXamlRoot;

    public MainWindow(WindowsLaunchOptions? launchOptions = null)
    {
        InitializeComponent();
        _isSpecialLaunch = launchOptions?.UsesReadmePage == true;
        _isCaptureLaunch = launchOptions?.ReadmeOutput is not null;
        if (Content is FrameworkElement root && launchOptions?.DemoTheme is { } theme)
            root.RequestedTheme = theme == "light" ? ElementTheme.Light : ElementTheme.Dark;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        if (_isSpecialLaunch)
        {
            AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1280,
                _isCaptureLaunch && launchOptions?.ReadmeScreen != WindowsReadmeScreen.Tray ? 1400 : 900));
        }
        else
        {
            ApplyWindowBounds(center: true);
            AppWindow.Changed += AppWindow_Changed;
            RootFrame.Loaded += RootFrame_Loaded;
            Closed += MainWindow_Closed;
        }
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

    public void ShowManagementWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        AppWindow.Show();
        Activate();
    }

    public void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    private bool _isExiting;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    private void ApplyWindowBounds(bool center = false)
    {
        if (_isSpecialLaunch || _updatingWindowBounds || AppWindow.Presenter is not OverlappedPresenter presenter)
            return;

        _updatingWindowBounds = true;
        try
        {
            // Windowing sizes are physical pixels. Keep the requested management-window
            // limits in DIPs so they remain consistent when moving between monitors.
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = Math.Max(1.0, GetDpiForWindow(handle) / 96.0);
            var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var margin = (int)Math.Round(16 * scale);
            var maxWidth = Math.Min((int)Math.Round(920 * scale), Math.Max(1, workArea.Width - margin * 2));
            var maxHeight = Math.Min((int)Math.Round(940 * scale), Math.Max(1, workArea.Height - margin * 2));
            var minWidth = Math.Min((int)Math.Round(740 * scale), maxWidth);
            var minHeight = Math.Min((int)Math.Round(600 * scale), maxHeight);
            var bounds = (minWidth, minHeight, maxWidth, maxHeight);
            var boundsChanged = _windowBounds != bounds;

            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
            presenter.IsResizable = true;
            if (boundsChanged)
            {
                // Clear old minima first: a smaller work area can be below a previous
                // monitor's minimum, and the new maximum must never conflict with it.
                presenter.PreferredMinimumWidth = null;
                presenter.PreferredMinimumHeight = null;
                presenter.PreferredMaximumWidth = maxWidth;
                presenter.PreferredMaximumHeight = maxHeight;
                presenter.PreferredMinimumWidth = minWidth;
                presenter.PreferredMinimumHeight = minHeight;
                _windowBounds = bounds;
            }

            if (presenter.State == OverlappedPresenterState.Minimized)
                return;
            if (presenter.State == OverlappedPresenterState.Maximized)
                presenter.Restore(activateWindow: false);

            var width = center ? maxWidth : Math.Clamp(AppWindow.Size.Width, minWidth, maxWidth);
            var height = center ? maxHeight : Math.Clamp(AppWindow.Size.Height, minHeight, maxHeight);
            // Do not constrain every drag position: the window must be able to cross
            // a monitor edge before Windows assigns it to the neighboring display.
            var x = center ? workArea.X + (workArea.Width - width) / 2
                : boundsChanged ? Math.Clamp(AppWindow.Position.X, workArea.X, workArea.X + workArea.Width - width)
                : AppWindow.Position.X;
            var y = center ? workArea.Y + (workArea.Height - height) / 2
                : boundsChanged ? Math.Clamp(AppWindow.Position.Y, workArea.Y, workArea.Y + workArea.Height - height)
                : AppWindow.Position.Y;
            if (AppWindow.Size.Width != width || AppWindow.Size.Height != height
                || AppWindow.Position.X != x || AppWindow.Position.Y != y)
                AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(x, y, width, height));
        }
        finally
        {
            _updatingWindowBounds = false;
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)
            ApplyWindowBounds();
    }

    private void RootFrame_Loaded(object sender, RoutedEventArgs e)
    {
        if (_sizingXamlRoot is not null || RootFrame.XamlRoot is not { } root)
            return;
        _sizingXamlRoot = root;
        root.Changed += SizingXamlRoot_Changed;
    }

    private void SizingXamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => ApplyWindowBounds();

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        AppWindow.Changed -= AppWindow_Changed;
        RootFrame.Loaded -= RootFrame_Loaded;
        if (_sizingXamlRoot is { } root)
            root.Changed -= SizingXamlRoot_Changed;
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        ApplyWindowBounds();
        if (Page is { } page)
        {
            _ = page.RefreshUsageIfStaleAsync();
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isExiting || _isCaptureLaunch)
        {
            return;
        }

        args.Cancel = true;
        AppWindow.Hide();
    }
}
