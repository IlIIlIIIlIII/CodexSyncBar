using System.Runtime.InteropServices.WindowsRuntime;
using CodexSyncBar.Windows.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace CodexSyncBar_Windows;

public sealed partial class ReadmeDemoPage : Page
{
    private readonly TaskCompletionSource<bool> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WindowsReadmeScreen _screen = WindowsReadmeScreen.Popover;

    public ReadmeDemoPage()
    {
        InitializeComponent();
    }

    private DemoDashboardRuntime Runtime => ((App)Application.Current).Demo!;
    private DashboardSnapshot? _snapshot;
    public Task Ready => _ready.Task;

    private async Task RenderAsync()
    {
        _snapshot = await Runtime.GetSnapshotAsync();
        var selected = (AccountPicker.SelectedItem as DashboardAccount)?.ProfileId;
        AccountPicker.ItemsSource = _snapshot.Accounts;
        AccountPicker.SelectedItem = _snapshot.Accounts.FirstOrDefault(a => a.ProfileId == selected)
            ?? _snapshot.Accounts.FirstOrDefault();
        ActiveAccountText.Text = "현재 적용 계정 · " + _snapshot.Accounts.First(a => a.ProfileId == _snapshot.ActiveProfileId).DisplayName;
        DevicesText.Text = string.Join("\n", _snapshot.Devices.Select(d => $"✓  {d.DisplayName} · 연결됨"));
        StatusText.Text = _snapshot.Operation?.Message ?? "전환할 계정을 선택하고 적용 버튼을 눌러 주세요.";
        ApplyButton.IsEnabled = !_snapshot.IsBusy;
        UpdatedText.Text = $"갱신 {_snapshot.UpdatedAt.ToLocalTime():HH:mm:ss}";
        RenderSelection();
    }

    private void RenderSelection()
    {
        if (AccountPicker.SelectedItem is not DashboardAccount { Usage: { } usage }) return;
        SessionBar.Value = 100 - (usage.Session?.UsedPercent ?? 0);
        WeeklyBar.Value = 100 - (usage.Weekly?.UsedPercent ?? 0);
        SessionValue.Text = $"{SessionBar.Value:0}% 남음";
        WeeklyValue.Text = $"{WeeklyBar.Value:0}% 남음";
        UsageText.Text = "사용량을 확인했습니다.";
        CreditsText.Text = $"{usage.ResetCredits}개";
    }

    private void AccountPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderSelection();
    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await Runtime.RefreshUsageAsync();
    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccountPicker.SelectedItem is not DashboardAccount account || _snapshot is null) return;
        try { await Runtime.SwitchAccountAsync(account.ProfileId, _snapshot.ConfigurationRevision); }
        catch (Exception error) { StatusText.Text = error.Message; }
    }
    private void Runtime_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(async () => await RenderAsync());

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _screen = e.Parameter is WindowsReadmeScreen screen
            ? screen
            : WindowsReadmeScreen.Popover;
        PopoverPanel.Visibility = _screen != WindowsReadmeScreen.Settings
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsPanel.Visibility = _screen == WindowsReadmeScreen.Settings
            ? Visibility.Visible
            : Visibility.Collapsed;
        Loaded += ReadmeDemoPage_Loaded;
    }

    public async Task CapturePngAsync(string outputPath)
    {
        await Ready;
        await Task.Delay(150);
        RootGrid.UpdateLayout();

        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(RootGrid);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            throw new InvalidOperationException("README 캡처 화면 크기가 올바르지 않습니다.");
        }

        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outputPath)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(outputPath), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        stream.Size = 0;
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var pixels = await bitmap.GetPixelsAsync();
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth,
            (uint)bitmap.PixelHeight,
            96,
            96,
            pixels.ToArray());
        await encoder.FlushAsync();
    }

    private async void ReadmeDemoPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ReadmeDemoPage_Loaded;
        Runtime.Changed += Runtime_Changed;
        Unloaded += (_, _) => Runtime.Changed -= Runtime_Changed;
        await RenderAsync();
        _ready.TrySetResult(true);
    }
}
