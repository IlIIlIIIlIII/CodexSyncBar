using System.Runtime.InteropServices;
using ClaudeSyncBar.App.Controls;
using ClaudeSyncBar.App.Model;
using ClaudeSyncBar.App.Pages;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeSyncBar.App;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["usage"] = typeof(UsagePage),
        ["switch"] = typeof(SwitchPage),
        ["accounts"] = typeof(AccountsPage),
        ["devices"] = typeof(DevicesPage),
        ["tokens"] = typeof(TokensPage),
        ["settings"] = typeof(SettingsPage),
    };

    private readonly App _app;
    private readonly DispatcherQueueTimer _noticeTimer;
    private bool _exiting;
    private bool _selecting;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Title = "Claude SyncBar";
        if (app.Options.Theme is "light" or "dark")
            Root.RequestedTheme = app.Options.Theme == "light" ? ElementTheme.Light : ElementTheme.Dark;
        // A capture has no Mica behind it; the fallback colour Windows itself uses stands in.
        if (app.Options.CapturePath is not null)
            Root.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(app.Options.Theme == "dark" ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32) : Microsoft.UI.ColorHelper.FromArgb(255, 243, 243, 243));
        SizeWindow();
        AppWindow.Closing += (_, args) =>
        {
            // Closing the window keeps the app in the tray; quitting is explicit.
            if (_exiting || _app.Options.CapturePath is not null) return;
            args.Cancel = true;
            AppWindow.Hide();
        };
        _noticeTimer = DispatcherQueue.CreateTimer();
        _noticeTimer.Interval = TimeSpan.FromSeconds(6);
        _noticeTimer.Tick += (_, _) => { Notice.IsOpen = false; _noticeTimer.Stop(); };
        app.State.Changed += Render;
        app.State.Notice += ShowNotice;
        // "device:<id>" opens one device's page, the way a capture asks for it.
        if (app.Options.Page is { } start && start.StartsWith("device:", StringComparison.Ordinal))
        {
            Navigate("devices");
            Navigate("device", start["device:".Length..]);
        }
        else Navigate(app.Options.Page is { } page && Pages.ContainsKey(page) ? page : "usage");
        Render();
    }

    public FrameworkElement RootElement => Root;

    private string _page = "";
    private string? _previous;

    /// <summary>Shows a page. "device" opens one device's detail under the devices page, with the back button.</summary>
    public void Navigate(string tag, object? parameter = null)
    {
        if (tag == _page && parameter is null) return;
        Page page = tag == "device" ? new DeviceDetailPage(parameter) : (Page)Activator.CreateInstance(Pages.GetValueOrDefault(tag, typeof(UsagePage)))!;
        // From one device page to another, back still leads to where the first was opened from.
        _previous = tag == "device" ? (_page == "device" ? _previous : _page) : null;
        _page = tag;
        ContentHost.Content = page;
        var selected = tag == "device" ? "devices" : tag;
        _selecting = true;
        Navigation.SelectedItem = Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>()
            .FirstOrDefault(item => item.Tag as string == selected);
        _selecting = false;
        AppTitleBar.IsBackButtonEnabled = _previous is not null;
    }

    public void GoBack()
    {
        if (_previous is { } previous) Navigate(previous);
    }

    public void ShowAndActivate()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        AppWindow.Show();
        Activate();
    }

    public void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_selecting || args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;
        Navigate(tag);
    }

    private void AppTitleBar_BackRequested(TitleBar sender, object args) => GoBack();

    private void Render()
    {
        var state = _app.State;
        var active = state.Active;
        UserAvatar.Content = Ui.Avatar(AppState.Int(active?["id"]), active is null ? "?" : AppState.AccountName(active), 40);
        UserName.Text = active is null ? "적용된 계정 없음" : AppState.AccountName(active);
        var email = AppState.Str(active?["email"]);
        UserDetail.Text = active is null ? "계정을 추가하거나 적용해 주세요" : "이 PC에 적용됨" + (email is { Length: > 0 } && email != UserName.Text ? " · " + email : "");
        var needsLogin = state.Accounts.Count(AppState.NeedsLogin);
        AccountsBadge.Value = needsLogin;
        AccountsBadge.Visibility = needsLogin > 0 ? Visibility.Visible : Visibility.Collapsed;
        // What the app is doing reads right after its name, as the design has it; the title
        // bar's own content area would centre it.
        string status;
        if (state.Busy || state.RefreshRequested) status = state.Refreshing ? "새로고침 중…" : AppState.Str(state.Operation?["message"]) ?? state.BusyLabel;
        else
        {
            var updated = state.Accounts.Select(account => QuotaFormat.Time(account["usage"]?["updatedAt"])).OfType<DateTimeOffset>().DefaultIfEmpty().Max();
            status = state.Error is { } error ? error : updated == default ? "" : $"{updated.ToLocalTime():HH:mm} 갱신";
        }

        var subtitle = string.Join(" · ", new[] { _app.Options.Demo ? "데모 · 가상 데이터" : null, status }.Where(part => !string.IsNullOrEmpty(part)));
        if (AppTitleBar.Subtitle != subtitle) AppTitleBar.Subtitle = subtitle;
    }

    private void ShowNotice(string message, NoticeKind kind)
    {
        Notice.Message = message;
        Notice.Severity = kind switch
        {
            NoticeKind.Success => InfoBarSeverity.Success,
            NoticeKind.Warning => InfoBarSeverity.Warning,
            NoticeKind.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        Notice.IsOpen = true;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    // The window opens at 1040 x 760 DIP and keeps room for the two quota columns.
    private void SizeWindow()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var scale = Math.Max(1.0, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        presenter.PreferredMinimumWidth = (int)Math.Round(740 * scale);
        presenter.PreferredMinimumHeight = (int)Math.Round(600 * scale);
        // A design capture may ask for a whole page, taller than the screen.
        var asked = _app.Options.WindowSize;
        var width = asked is { } size ? (int)Math.Round(size.Width * scale) : Math.Min((int)Math.Round(1040 * scale), area.Width - 32);
        var height = asked is { } tall ? (int)Math.Round(tall.Height * scale) : Math.Min((int)Math.Round(760 * scale), area.Height - 32);
        AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
