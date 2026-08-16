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

    public Task Ready => _ready.Task;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _screen = e.Parameter is WindowsReadmeScreen screen
            ? screen
            : WindowsReadmeScreen.Popover;
        PopoverPanel.Visibility = _screen == WindowsReadmeScreen.Popover
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

        var file = await StorageFile.GetFileFromPathAsync(outputPath);
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

    private void ReadmeDemoPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ReadmeDemoPage_Loaded;
        _ready.TrySetResult(true);
    }
}
