using CodexSyncBar.Windows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexSyncBar_Windows;

public sealed partial class MainPage
{
    private async Task<bool> EnsureSshTrustAsync(SshDeviceConfiguration device)
    {
        var trust = new SshHostTrust();
        var key = await trust.InspectAsync(device);
        if (key is null) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "처음 연결하는 SSH 서버",
            Content = new TextBlock
            {
                Text = $"{device.DisplayLabel} · {key.Host}:{key.Port}\n\n서버 호스트 키 지문\n{key.Fingerprint}\n\n서버 관리자에게 지문을 확인한 뒤 등록하세요. 이후에는 같은 서버 키인지 자동으로 검증합니다.",
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 480,
            },
            PrimaryButtonText = "신뢰하고 연결", SecondaryButtonText = "취소", DefaultButton = ContentDialogButton.Secondary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        await trust.TrustAsync(device, key);
        return true;
    }

    private async void ManageCliButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetBusy(true);
        try
        {
            var selected = _selectedDeviceId ?? "windows";
            var ssh = _configuration.Devices.FirstOrDefault(item => item.Id == selected);
            var wsl = _wslConfigurationStore.Load().FirstOrDefault(item => item.Id == selected);
            ICliManagementService service;
            string name;
            if (ssh is not null)
            {
                if (!await EnsureSshTrustAsync(ssh)) return;
                name = ssh.DisplayLabel;
                service = new RemoteCliManagementService((update, token) => _sshDeviceService.RunCodexHelperAsync(ssh, update, token));
            }
            else if (wsl is not null)
            {
                name = wsl.Distribution;
                service = new RemoteCliManagementService((update, token) => _wslDeviceService.RunCodexHelperAsync(wsl, update, token));
            }
            else if (selected == "windows")
            {
                name = "이 Windows PC";
                service = new WindowsCliManagementService(_paths, _localSwitchService);
            }
            else throw new CodexSyncBarException("CLI를 관리할 장치를 선택해 주세요.");

            var info = await service.InspectAsync();
            var details = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
            var resultText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            void Render(CliInstallation current) => details.Text = $"현재 버전: {(string.IsNullOrEmpty(current.Version) ? "확인 전" : current.Version)}\n설치 경로: {(string.IsNullOrEmpty(current.Path) ? "확인 전" : current.Path)}\n설치 방식: {current.Manager}\n{current.Notice}";
            Render(info);
            var panel = new StackPanel { Spacing = 16, MinWidth = 360, MaxWidth = 520 };
            panel.Children.Add(details);
            panel.Children.Add(new TextBlock { Text = "업데이트 후 이 장치의 Codex 연결을 다시 연결합니다. 다른 장치는 변경하지 않습니다.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(progress);
            panel.Children.Add(resultText);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = $"Codex CLI 관리 · {name}", Content = panel,
                PrimaryButtonText = info.Version == "미설치" ? "설치" : "업데이트", SecondaryButtonText = "닫기",
                IsPrimaryButtonEnabled = info.CanUpdate,
            };
            var updating = false;
            dialog.Closing += (_, args) => args.Cancel = updating;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;
                var deferral = args.GetDeferral();
                updating = true;
                dialog.IsPrimaryButtonEnabled = false;
                dialog.IsSecondaryButtonEnabled = false;
                progress.Visibility = Visibility.Visible;
                resultText.Text = "공식 최신 버전으로 업데이트하고 있습니다…";
                try
                {
                    using var mutation = await ControllerMutationLock.AcquireAsync(_paths);
                    var result = await service.UpdateAsync();
                    resultText.Text = result.DisplayText;
                    try { info = await service.InspectAsync(); Render(info); }
                    catch { resultText.Text += "\n최신 설치 정보 조회는 다시 시도해 주세요."; }
                }
                catch (Exception error) { resultText.Text = error.Message; }
                finally
                {
                    updating = false;
                    progress.Visibility = Visibility.Collapsed;
                    dialog.IsPrimaryButtonEnabled = info.CanUpdate;
                    dialog.IsSecondaryButtonEnabled = true;
                    deferral.Complete();
                }
            };
            await dialog.ShowAsync();
        }
        catch (Exception error) { SetBanner(error.Message, true); }
        finally { SetBusy(false); }
    }
}
