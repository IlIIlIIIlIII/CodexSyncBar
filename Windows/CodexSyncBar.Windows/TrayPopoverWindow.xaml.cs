using System.Runtime.InteropServices;
using CodexSyncBar.Windows.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace CodexSyncBar_Windows;

public sealed partial class TrayPopoverWindow : Window, IDisposable
{
    private const int LogicalWidth = 410;
    private const int LogicalHeight = 700;
    private readonly MainPage _page;
    private readonly Action _showMainWindow;
    private readonly Action _exitApplication;
    private readonly IntPtr _windowHandle;
    private DateTimeOffset _lastAutomaticHide = DateTimeOffset.MinValue;
    private bool _isVisible;
    private bool _isDisposing;
    private int? _selectedProfileId;

    public TrayPopoverWindow(
        MainPage page,
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
        var scale = Math.Max(1.0, GetDpiForWindow(_windowHandle) / 96.0);
        var displayArea = DisplayArea.GetFromPoint(cursor, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
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

    private void Render(TrayPopoverSnapshot state)
    {
        _selectedProfileId = state.SelectedProfileId;
        TrayStatusText.Text = state.TrayTitle;
        AccountCountText.Text = $"{state.Accounts.Count}개";
        SelectedAliasText.Text = state.SelectedAlias;
        SelectedEmailText.Text = state.SelectedEmail;
        PlanText.Text = state.Plan;
        AuthenticationText.Text = state.AuthenticationText;
        AuthenticationText.Foreground = state.AuthenticationText == "인증 정상"
            ? Brush(0x4A, 0xDE, 0x80)
            : Brush(0xFB, 0xBF, 0x24);
        ActiveBadge.Visibility = state.SelectedProfileId is not null
            && state.SelectedProfileId == state.ActiveProfileId
                ? Visibility.Visible
                : Visibility.Collapsed;

        BannerBorder.Visibility = string.IsNullOrWhiteSpace(state.Banner)
            ? Visibility.Collapsed
            : Visibility.Visible;
        BannerText.Text = state.Banner ?? string.Empty;
        BannerText.Foreground = state.BannerIsError
            ? Brush(0xFB, 0x92, 0x3C)
            : Brush(0xBF, 0xD8, 0xFF);
        BannerBorder.Background = state.BannerIsError
            ? Brush(0x35, 0x20, 0x18)
            : Brush(0x18, 0x22, 0x38);
        BannerBorder.BorderBrush = state.BannerIsError
            ? Brush(0x72, 0x3A, 0x1E)
            : Brush(0x30, 0x48, 0x6F);

        RenderAccounts(state.Accounts);
        RenderUsage(state);
        CreditsText.Text = state.CreditsText;
        ResetCreditsText.Text = state.ResetCreditsText;
        RenderDevices(state.Devices);

        RefreshButton.IsEnabled = !state.IsBusy;
        UsageProgressRing.IsActive = state.IsBusy;
        UsageProgressRing.Visibility = state.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        ApplyProgressRing.IsActive = state.IsBusy;
        ApplyProgressRing.Visibility = state.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        ApplyIcon.Visibility = state.IsBusy ? Visibility.Collapsed : Visibility.Visible;

        var alreadyApplied = state.SelectedProfileId is not null
            && state.SelectedProfileId == state.ActiveProfileId
            && !state.HasDeviceMismatch;
        ApplyButton.IsEnabled = state.CanApply && !alreadyApplied;
        ApplyButtonText.Text = state.IsBusy
            ? "계정과 장치 상태를 처리하는 중…"
            : alreadyApplied
                ? "이미 모든 장치에 적용됨"
                : $"모든 장치를 {state.SelectedAlias} 계정으로 전환";
        ApplyButton.Background = alreadyApplied
            ? Brush(0x1F, 0x6F, 0x46)
            : Brush(0x25, 0x63, 0xEB);
    }

    private void RenderAccounts(IReadOnlyList<TrayAccountSnapshot> accounts)
    {
        AccountsPanel.Children.Clear();
        for (var index = 0; index < accounts.Count; index += 2)
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(CreateAccountButton(accounts[index], 0));
            if (index + 1 < accounts.Count)
            {
                row.Children.Add(CreateAccountButton(accounts[index + 1], 1));
            }

            AccountsPanel.Children.Add(row);
        }

        if (accounts.Count == 0)
        {
            AccountsPanel.Children.Add(new TextBlock
            {
                Text = "전체 관리 창에서 계정을 추가해 주세요.",
                FontSize = 10,
                Foreground = Brush(0x8C, 0x95, 0xA5),
                Margin = new Thickness(4, 6, 4, 6),
            });
        }
    }

    private Button CreateAccountButton(TrayAccountSnapshot account, int column)
    {
        var button = new Button
        {
            Tag = account.Id,
            MinHeight = 54,
            Padding = new Thickness(9, 7, 8, 7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = account.IsSelected ? Brush(0x25, 0x63, 0xEB) : Brush(0x17, 0x1A, 0x21),
            BorderBrush = account.IsSelected ? Brush(0x4C, 0x88, 0xF7) : Brush(0x2A, 0x30, 0x3B),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
        };
        Grid.SetColumn(button, column);
        button.Click += AccountButton_Click;
        ToolTipService.SetToolTip(button, account.Email);

        var content = new Grid { ColumnSpacing = 7 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var avatar = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = account.IsSelected ? Brush(0x4C, 0x7D, 0xE8) : Brush(0x26, 0x3A, 0x5F),
            Child = new TextBlock
            {
                Text = account.ShortName,
                FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        content.Children.Add(avatar);

        var labels = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = account.Alias,
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        labels.Children.Add(new TextBlock
        {
            Text = account.StatusText,
            FontSize = 8,
            Foreground = account.NeedsLogin || account.IsPending
                ? Brush(0xFB, 0xBF, 0x24)
                : account.IsActive
                    ? Brush(0x4A, 0xDE, 0x80)
                    : Brush(0x8C, 0x95, 0xA5),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(labels, 1);
        content.Children.Add(labels);

        var value = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Right };
        value.Children.Add(new TextBlock
        {
            Text = account.UsageText,
            FontSize = 9,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        if (account.IsActive)
        {
            value.Children.Add(new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 9,
                Foreground = account.IsSelected ? Brush(0xFF, 0xFF, 0xFF) : Brush(0x4A, 0xDE, 0x80),
                HorizontalAlignment = HorizontalAlignment.Right,
            });
        }

        Grid.SetColumn(value, 2);
        content.Children.Add(value);
        button.Content = content;
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
            [UsageDisplayItem.SparkFiveHour] = snapshot?.SparkSession,
            [UsageDisplayItem.SparkWeekly] = snapshot?.SparkWeekly,
        };

        foreach (var item in state.VisibleUsageItems)
        {
            UsagePanel.Children.Add(CreateQuotaRow(item, windows[item]));
        }

        if (state.VisibleUsageItems.Count == 0)
        {
            UsagePanel.Children.Add(new TextBlock
            {
                Text = "전체 관리 창의 설정에서 표시할 사용량 항목을 선택할 수 있습니다.",
                FontSize = 9,
                Foreground = Brush(0x8C, 0x95, 0xA5),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        UsageMessageText.Visibility = snapshot is null || !string.IsNullOrWhiteSpace(state.UsageError)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UsageMessageText.Text = !string.IsNullOrWhiteSpace(state.UsageError)
            ? state.UsageError
            : snapshot is null
                ? "사용량 확인 중…"
                : string.Empty;
        UsageMessageText.Foreground = !string.IsNullOrWhiteSpace(state.UsageError)
            ? Brush(0xFB, 0x92, 0x3C)
            : Brush(0x8C, 0x95, 0xA5);
        UsageUpdatedText.Text = snapshot is null
            ? string.Empty
            : $"{snapshot.UpdatedAt.ToLocalTime():HH:mm} 갱신";
    }

    private static FrameworkElement CreateQuotaRow(UsageDisplayItem item, UsageWindow? window)
    {
        var remaining = window?.RemainingPercent;
        var tint = remaining is null
            ? Brush(0x69, 0x73, 0x86)
            : remaining <= 10
                ? Brush(0xF8, 0x71, 0x71)
                : remaining <= 25
                    ? Brush(0xFB, 0xBF, 0x24)
                    : item is UsageDisplayItem.SparkFiveHour or UsageDisplayItem.SparkWeekly
                        ? Brush(0x60, 0xA5, 0xFA)
                        : Brush(0x22, 0xD3, 0xEE);
        var container = new StackPanel { Spacing = 4 };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = item.Title(),
            FontSize = 9,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(0xA6, 0xAF, 0xBE),
        });
        var reset = new TextBlock
        {
            Text = window is null
                ? "한도 정보 없음"
                : UsageFormatting.ResetDescription(window.ResetsAt, DateTimeOffset.UtcNow),
            FontSize = 8,
            Foreground = Brush(0x69, 0x73, 0x86),
        };
        Grid.SetColumn(reset, 1);
        header.Children.Add(reset);
        container.Children.Add(header);
        container.Children.Add(new TextBlock
        {
            Text = remaining is null ? "—" : $"{Math.Round(remaining.Value):0}% 남음",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = remaining is null ? Brush(0x69, 0x73, 0x86) : Brush(0xF5, 0xF7, 0xFA),
        });
        container.Children.Add(new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = remaining ?? 0,
            Height = 7,
            Foreground = tint,
            Background = Brush(0x29, 0x2E, 0x38),
        });
        return container;
    }

    private void RenderDevices(IReadOnlyList<TrayDeviceSnapshot> devices)
    {
        DevicesPanel.Children.Clear();
        var reachableCount = devices.Count(device => device.IsReachable);
        DeviceCountText.Text = $"{reachableCount}/{devices.Count}대 연결";
        foreach (var device in devices.Take(4))
        {
            if (DevicesPanel.Children.Count > 0)
            {
                DevicesPanel.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 1, 0, 1),
                    Background = Brush(0x25, 0x2A, 0x33),
                });
            }

            var row = new Grid { Padding = new Thickness(2, 7, 2, 7), ColumnSpacing = 7 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon
            {
                Glyph = device.Id == "windows" ? "\uE770" : "\uE977",
                FontSize = 11,
                Foreground = device.IsReachable ? Brush(0x4A, 0xDE, 0x80) : Brush(0xF8, 0x71, 0x71),
            });
            var name = new TextBlock
            {
                Text = device.DisplayName,
                FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var details = new TextBlock
            {
                Text = $"{device.AccountText} · {device.StateText}",
                FontSize = 8,
                Foreground = device.IsReachable ? Brush(0x8C, 0x95, 0xA5) : Brush(0xF8, 0x71, 0x71),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(details, 2);
            row.Children.Add(details);
            DevicesPanel.Children.Add(row);
        }

        if (devices.Count > 4)
        {
            DevicesPanel.Children.Add(new TextBlock
            {
                Text = $"외 {devices.Count - 4}대 · 전체 관리에서 확인",
                FontSize = 8,
                Foreground = Brush(0x69, 0x73, 0x86),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 5),
            });
        }

        if (devices.Count == 0)
        {
            DevicesPanel.Children.Add(new TextBlock
            {
                Text = "장치 상태를 확인하는 중…",
                FontSize = 9,
                Foreground = Brush(0x8C, 0x95, 0xA5),
                Margin = new Thickness(2, 8, 2, 8),
            });
        }
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

    private static SolidColorBrush Brush(byte red, byte green, byte blue) =>
        new(Color.FromArgb(0xFF, red, green, blue));

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);
}
