using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using CodexSyncBar.Windows.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace CodexSyncBar_Windows;

public sealed partial class TrayPopoverWindow : Window, IDisposable
{
    private const int LogicalWidth = 440;
    private const int LogicalHeight = 700;
    private readonly ITrayDashboard _page;
    private readonly Action _showMainWindow;
    private readonly Action _exitApplication;
    private readonly IntPtr _windowHandle;
    private DateTimeOffset _lastAutomaticHide = DateTimeOffset.MinValue;
    private bool _isVisible;
    private bool _isDisposing;
    private int? _selectedProfileId;

    internal TrayPopoverWindow(
        ITrayDashboard page,
        Action showMainWindow,
        Action exitApplication)
    {
        InitializeComponent();
        _page = page;
        _showMainWindow = showMainWindow;
        _exitApplication = exitApplication;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);

        AppWindow.Title = "Codex SyncBar 빠른 보기";
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        Activated += TrayPopoverWindow_Activated;
        AppWindow.Closing += AppWindow_Closing;
        _page.TrayStateChanged += Page_TrayStateChanged;
        WindowSurface.ActualThemeChanged += (_, _) => Render(_page.CreateTrayPopoverSnapshot());
        WindowSurface.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape)
            {
                Hide();
                args.Handled = true;
            }
        };
    }

    internal void SetTheme(ElementTheme theme) => WindowSurface.RequestedTheme = theme;

    internal async Task CapturePngAsync(string outputPath)
    {
        // Let initial determinate progress transitions settle before capturing a fixture.
        await Task.Delay(1200);
        WindowSurface.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(WindowSurface);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            throw new InvalidOperationException("트레이 화면 캡처 크기가 올바르지 않습니다.");
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outputPath)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(outputPath), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        stream.Size = 0;
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
        await encoder.FlushAsync();
    }

    public void ToggleAt(PointInt32 cursorPosition)
    {
        if (_isVisible)
        {
            Hide();
            return;
        }

        // A click on the tray icon first deactivates this window and then sends
        // the tray click. Do not immediately reopen after that automatic hide.
        if (DateTimeOffset.UtcNow - _lastAutomaticHide < TimeSpan.FromMilliseconds(350))
        {
            return;
        }

        ShowAt(cursorPosition);
    }

    public void ShowAt(PointInt32 anchorPosition)
    {
        Render(_page.CreateTrayPopoverSnapshot());
        PositionNear(anchorPosition);
        AppWindow.Show();
        _isVisible = true;
        Activate();
        RefreshButton.Focus(FocusState.Programmatic);
        _ = _page.RefreshUsageIfStaleAsync();
    }

    public void Hide()
    {
        if (!_isVisible)
        {
            return;
        }

        _isVisible = false;
        AppWindow.Hide();
    }

    public void Dispose()
    {
        if (_isDisposing)
        {
            return;
        }

        _isDisposing = true;
        _page.TrayStateChanged -= Page_TrayStateChanged;
        Activated -= TrayPopoverWindow_Activated;
        AppWindow.Closing -= AppWindow_Closing;
        Close();
    }

    private void Page_TrayStateChanged(object? sender, EventArgs e)
    {
        if (!_isVisible || _isDisposing)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isVisible && !_isDisposing)
            {
                Render(_page.CreateTrayPopoverSnapshot());
            }
        });
    }

    private void TrayPopoverWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (!_isVisible || _isDisposing || args.WindowActivationState != WindowActivationState.Deactivated)
        {
            return;
        }

        _lastAutomaticHide = DateTimeOffset.UtcNow;
        Hide();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isDisposing)
        {
            return;
        }

        args.Cancel = true;
        Hide();
    }

    private void PositionNear(PointInt32 cursor)
    {
        var displayArea = DisplayArea.GetFromPoint(cursor, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        // Move the hidden HWND first so its per-monitor DPI belongs to the target display.
        AppWindow.Move(new PointInt32(workArea.X, workArea.Y));
        var scale = Math.Max(1.0, GetDpiForWindow(_windowHandle) / 96.0);
        var margin = (int)Math.Round(8 * scale);
        var width = Math.Min((int)Math.Round(LogicalWidth * scale), workArea.Width - margin * 2);
        var height = Math.Min((int)Math.Round(LogicalHeight * scale), workArea.Height - margin * 2);

        var x = cursor.X - width + (int)Math.Round(22 * scale);
        var y = cursor.Y - height - (int)Math.Round(12 * scale);
        if (cursor.Y <= workArea.Y + margin || y < workArea.Y)
        {
            y = cursor.Y + (int)Math.Round(12 * scale);
        }

        x = Math.Clamp(x, workArea.X + margin, workArea.X + workArea.Width - width - margin);
        y = Math.Clamp(y, workArea.Y + margin, workArea.Y + workArea.Height - height - margin);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private Style TrayStyle(string key) => (Style)WindowSurface.Resources[key];

    private TextBlock Label(string text, string style = "TrayBody", double? size = null, bool strong = false)
    {
        var label = new TextBlock { Text = text, Style = TrayStyle(style), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        if (size is { } value) label.FontSize = value;
        if (strong) label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        return label;
    }

    private void Render(TrayPopoverSnapshot state)
    {
        _selectedProfileId = state.SelectedProfileId;
        TrayStatusText.Text = state.TrayTitle;
        AccountCountText.Text = $"{state.Accounts.Count}개";
        SelectedAliasText.Text = state.SelectedAlias;
        SelectedEmailText.Text = state.SelectedEmail;
        PlanText.Text = state.Plan;
        AuthenticationText.Text = state.AuthenticationText;
        AuthenticationText.Style = TrayStyle(state.AuthenticationText == "인증 정상" ? "TraySuccess" : "TrayCaution");
        ActiveBadge.Visibility = state.SelectedProfileId is not null && state.SelectedProfileId == state.ActiveProfileId
            ? Visibility.Visible : Visibility.Collapsed;
        BannerBorder.Visibility = string.IsNullOrWhiteSpace(state.Banner) ? Visibility.Collapsed : Visibility.Visible;
        BannerText.Text = state.Banner ?? string.Empty;
        BannerText.Style = TrayStyle(state.BannerIsError ? "TrayCritical" : "TrayCaption");
        RenderAccounts(state.Accounts);
        RenderUsage(state);
        CreditsText.Text = state.Usage?.UnlimitedCredits == true ? "추가 크레딧 무제한"
            : state.Usage?.CreditBalance is { } credits ? $"추가 크레딧 {credits:0.##}" : string.Empty;
        CreditsText.Visibility = string.IsNullOrWhiteSpace(CreditsText.Text) ? Visibility.Collapsed : Visibility.Visible;
        RenderResetCredits(state.Usage);
        RenderDevices(state.Devices);
        RefreshButton.IsEnabled = !state.IsBusy;
        UsageProgressRing.IsActive = state.IsBusy;
        UsageProgressRing.Visibility = state.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        ApplyProgressRing.IsActive = state.IsBusy;
        ApplyProgressRing.Visibility = state.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        ApplyIcon.Visibility = state.IsBusy ? Visibility.Collapsed : Visibility.Visible;
        var alreadyApplied = state.SelectedProfileId is not null
            && state.SelectedProfileId == state.ActiveProfileId && !state.HasDeviceMismatch;
        ApplyButton.IsEnabled = state.CanApply && !alreadyApplied;
        ApplyButtonText.Text = state.IsBusy ? "처리 중…"
            : alreadyApplied ? "모든 장치에 적용됨" : "모든 장치에 적용";
        AutomationProperties.SetName(ApplyButton, $"{state.SelectedAlias} · {ApplyButtonText.Text}");
        ToolTipService.SetToolTip(ApplyButton, $"선택한 {state.SelectedAlias} 계정을 활성화한 모든 장치에 적용합니다.");
    }

    private void RenderAccounts(IReadOnlyList<TrayAccountSnapshot> accounts)
    {
        var focused = WindowSurface.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Button : null;
        var focusedAccountId = focused?.Tag is int profileId ? profileId : (int?)null;
        AccountsPanel.Children.Clear();
        for (var index = 0; index < accounts.Count; index += 2)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(CreateAccountButton(accounts[index], 0));
            if (index + 1 < accounts.Count) row.Children.Add(CreateAccountButton(accounts[index + 1], 1));
            AccountsPanel.Children.Add(row);
            foreach (var button in row.Children.OfType<Button>())
                if (focusedAccountId is not null && button.Tag is int id && id == focusedAccountId)
                    button.Focus(FocusState.Programmatic);
        }
        if (accounts.Count == 0)
        {
            var empty = Label("계정 및 장치 관리에서 계정을 추가해 주세요.", "TrayCaption");
            empty.TextWrapping = TextWrapping.Wrap;
            AccountsPanel.Children.Add(empty);
        }
    }

    private Button CreateAccountButton(TrayAccountSnapshot account, int column)
    {
        var button = new Button
        {
            Tag = account.Id, MinHeight = 60, Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        if (account.IsSelected) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        Grid.SetColumn(button, column);
        AutomationProperties.SetName(button, $"{account.Alias}, {account.StatusText}{(account.IsSelected ? ", 선택됨" : string.Empty)}");
        AutomationProperties.SetHelpText(button, "계정을 선택한 뒤 적용 버튼을 눌러야 장치의 인증이 변경됩니다.");
        button.Click += AccountButton_Click;
        ToolTipService.SetToolTip(button, $"{account.Alias} · {account.Email} · {account.UsageText}");
        var labels = new StackPanel { Spacing = 4 };
        labels.Children.Add(Label(account.Alias, account.IsSelected ? "TrayOnAccent" : "TrayBody", strong: true));
        labels.Children.Add(Label(account.StatusText,
            account.IsSelected ? "TrayOnAccent" : account.NeedsLogin || account.IsPending ? "TrayCaution"
                : account.IsActive ? "TraySuccess" : "TrayCaption", 12));
        button.Content = labels;
        return button;
    }

    private void RenderUsage(TrayPopoverSnapshot state)
    {
        UsagePanel.Children.Clear();
        var snapshot = state.Usage;
        var windows = new Dictionary<UsageDisplayItem, UsageWindow?>
        {
            [UsageDisplayItem.FiveHour] = snapshot?.Session,
            [UsageDisplayItem.CodexWeekly] = snapshot?.Weekly,
        };
        var visible = state.VisibleUsageItems
            .Where(item => windows.ContainsKey(item) && (item != UsageDisplayItem.FiveHour || snapshot?.Session is not null)).ToArray();
        foreach (var item in visible)
        {
            if (!windows.TryGetValue(item, out var window)) continue;
            var metric = CreateQuotaRow(item, window);
            Grid.SetColumn(metric, UsagePanel.Children.Count);
            if (visible.Length == 1) Grid.SetColumnSpan(metric, 2);
            UsagePanel.Children.Add(metric);
        }
        if (UsagePanel.Children.Count == 0)
        {
            var empty = Label(state.VisibleUsageItems.Count == 0 ? "설정에서 표시할 사용량을 선택해 주세요." : "표시할 한도 정보가 없습니다.", "TrayCaption");
            empty.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumnSpan(empty, 2);
            UsagePanel.Children.Add(empty);
        }
        UsageMessageText.Visibility = snapshot is null || !string.IsNullOrWhiteSpace(state.UsageError) ? Visibility.Visible : Visibility.Collapsed;
        UsageMessageText.Text = state.UsageError ?? (snapshot is null ? "사용량을 확인하고 있습니다…" : string.Empty);
        UsageMessageText.Style = TrayStyle(string.IsNullOrWhiteSpace(state.UsageError) ? "TrayCaption" : "TrayCaution");
        UsageUpdatedText.Text = snapshot is null ? string.Empty : $"{snapshot.UpdatedAt.ToLocalTime():HH:mm} 갱신";
    }

    private void RenderResetCredits(UsageSnapshot? usage)
    {
        ResetCreditCountText.Text = usage?.ResetCredits is { } count ? $"{count}개" : "—";
        var groups = UsageFormatting.ResetCreditExpiryGroups(usage?.ResetCreditExpirations ?? [], DateTimeOffset.UtcNow);
        var next = groups.FirstOrDefault();
        ResetCreditsText.Text = usage?.ResetCredits == 0 ? "보유한 초기화권이 없습니다."
            : next is null ? "만료 정보가 아직 제공되지 않았습니다." : $"{next.Count}개 · {next.RemainingText}";
        ResetCreditDateText.Text = next is null || usage?.ResetCredits == 0 ? string.Empty
            : $"만료 {next.ExpiresAtText}" + (groups.Count > 1 ? $"\n외 {groups.Count - 1}개 만료 일정 · 전체 관리에서 확인" : string.Empty);
        ResetCreditDateText.Visibility = string.IsNullOrEmpty(ResetCreditDateText.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private FrameworkElement CreateQuotaRow(UsageDisplayItem item, UsageWindow? window)
    {
        var remaining = window?.RemainingPercent;
        var container = new StackPanel { Spacing = 8 };
        container.Children.Add(Label(item.Title(), "TrayCaption"));
        var value = Label(remaining is null ? "—" : $"{Math.Round(remaining.Value):0}%", size: 28, strong: true);
        AutomationProperties.SetName(value, remaining is null ? $"{item.Title()} 한도 정보 없음" : $"{item.Title()} {Math.Round(remaining.Value):0}% 남음");
        container.Children.Add(value);
        var progress = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Value = remaining ?? 0,
            Style = TrayStyle(remaining <= 10 ? "TrayProgressCritical" : remaining <= 25 ? "TrayProgressCaution" : "TrayProgress"),
        };
        var resetText = window is null ? "한도 정보 없음" : UsageFormatting.QuotaResetDescription(window.ResetsAt, DateTimeOffset.UtcNow);
        AutomationProperties.SetName(progress, window is null ? $"{item.Title()} 한도 정보 없음" : $"{item.Title()} 잔여 사용량");
        AutomationProperties.SetItemStatus(progress, window is null ? "한도 정보 없음" : resetText);
        progress.Visibility = window is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetHelpText(progress, resetText);
        container.Children.Add(progress);
        var reset = Label(resetText, "TrayCaption");
        reset.TextWrapping = TextWrapping.Wrap;
        container.Children.Add(reset);
        return container;
    }

    private void RenderDevices(IReadOnlyList<TrayDeviceSnapshot> devices)
    {
        DevicesPanel.Children.Clear();
        DeviceCountText.Text = $"{devices.Count(device => device.IsReachable)}/{devices.Count}대 연결";
        foreach (var device in devices.Take(4))
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(Label(device.DisplayName, strong: true));
            var detail = Label($"{device.AccountText} · {device.StateText}", device.IsReachable ? "TrayCaption" : "TrayCritical");
            ToolTipService.SetToolTip(detail, detail.Text);
            row.Children.Add(detail);
            DevicesPanel.Children.Add(row);
        }
        if (devices.Count > 4) DevicesPanel.Children.Add(Label($"외 {devices.Count - 4}대 · 전체 관리에서 확인", "TrayCaption"));
        if (devices.Count == 0) DevicesPanel.Children.Add(Label("장치 상태를 확인하고 있습니다…", "TrayCaption"));
    }

    private async void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int profileId })
        {
            await _page.SelectFromTrayAsync(profileId);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await _page.RefreshTrayPopoverAsync();

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfileId is { } profileId)
        {
            await _page.ApplyFromTrayAsync(profileId);
        }
    }

    private void OpenMainWindowButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _showMainWindow();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _exitApplication();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);
}
