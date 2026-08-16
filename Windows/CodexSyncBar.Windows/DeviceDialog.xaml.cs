using CodexSyncBar.Windows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace CodexSyncBar_Windows;

public sealed partial class DeviceDialog : ContentDialog
{
    private readonly SshDeviceConfiguration? _existing;

    public DeviceDialog(SshDeviceConfiguration? existing = null)
    {
        InitializeComponent();
        _existing = existing;
        if (existing is not null)
        {
            Title = "SSH 장치 편집";
            DisplayNameBox.Text = existing.DisplayName;
            IdBox.Text = existing.Id;
            HostBox.Text = existing.Host;
            PortBox.Value = existing.Port;
            UsernameBox.Text = existing.Username;
            IdentityFileBox.Text = existing.IdentityFile ?? string.Empty;
            CertificateFileBox.Text = existing.CertificateFile ?? string.Empty;
            EnabledBox.IsChecked = existing.Enabled;
            AuthenticationBox.SelectedIndex = existing.Authentication switch
            {
                "privateKey" => 1,
                "password" => 2,
                _ => 0,
            };
        }

        // A new or already-disabled device must enter the activation flow
        // before it can join automatic account synchronization.
        EnabledBox.IsEnabled = existing?.Enabled == true;

        PrimaryButtonClick += OnPrimaryButtonClick;
        UpdateSecretVisibility();
    }

    public SshDeviceConfiguration? Device { get; private set; }

    public string Password => PasswordBox.Password;

    public string Passphrase => PassphraseBox.Password;

    public bool ClearPassword => ClearPasswordBox.IsChecked == true;

    public bool ClearPassphrase => ClearPassphraseBox.IsChecked == true;

    private void AuthenticationBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PasswordBox is not null)
        {
            UpdateSecretVisibility();
        }
    }

    private async void BrowseIdentityButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync();
        if (path is not null)
        {
            IdentityFileBox.Text = path;
        }
    }

    private async void BrowseCertificateButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync();
        if (path is not null)
        {
            CertificateFileBox.Text = path;
        }
    }

    private static async Task<string?> PickFileAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add("*");
        var window = ((App)Application.Current).MainWindow
            ?? throw new CodexSyncBarException("앱 창을 찾지 못했습니다.");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(window));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private void UpdateSecretVisibility()
    {
        var authentication = (AuthenticationBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        PasswordBox.Visibility = authentication == "password"
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClearPasswordBox.Visibility = authentication == "password"
            ? Visibility.Visible
            : Visibility.Collapsed;
        PassphraseBox.Visibility = authentication == "privateKey"
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClearPassphraseBox.Visibility = authentication == "privateKey"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            var authentication = (AuthenticationBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                ?? "openSSHConfig";
            Device = new SshDeviceConfiguration
            {
                Id = IdBox.Text.Trim().ToLowerInvariant(),
                CredentialId = _existing?.CredentialId,
                DisplayName = DisplayNameBox.Text.Trim(),
                Host = HostBox.Text.Trim(),
                Port = (int)PortBox.Value,
                Username = UsernameBox.Text.Trim(),
                Authentication = authentication,
                IdentityFile = string.IsNullOrWhiteSpace(IdentityFileBox.Text)
                    ? null
                    : IdentityFileBox.Text.Trim(),
                CertificateFile = string.IsNullOrWhiteSpace(CertificateFileBox.Text)
                    ? null
                    : CertificateFileBox.Text.Trim(),
                HasPassword = _existing?.HasPassword ?? false,
                HasKeyPassphrase = _existing?.HasKeyPassphrase ?? false,
                Enabled = EnabledBox.IsChecked == true,
            };

            if (string.IsNullOrWhiteSpace(Device.DisplayName))
            {
                Device.DisplayName = Device.Host;
            }
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            args.Cancel = true;
            ErrorText.Text = error.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
