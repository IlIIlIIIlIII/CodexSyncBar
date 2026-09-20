using CodexSyncBar.Windows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexSyncBar_Windows;

public sealed partial class MainPage
{
    private async void UpdateAllCliButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _configurationRecoveryNeeded) return;
        SetBusy(true);
        try
        {
            var targets = new List<CliUpdateTarget>
            {
                new("windows", "이 Windows PC", new WindowsCliManagementService(_paths, _localSwitchService)),
            };
            foreach (var device in _configuration.Devices)
            {
                targets.Add(new("ssh:" + device.Id, device.DisplayLabel + " · SSH",
                    new RemoteCliManagementService((update, token) => _sshDeviceService.RunCodexHelperAsync(device, update, token)),
                    async token => await new SshHostTrust().InspectAsync(device, token) is null ? null
                        : "서버 지문 확인이 필요합니다. 이 기기의 연결 테스트에서 호스트 키를 등록한 뒤 다시 시도해 주세요."));
            }
            foreach (var device in _wslConfigurationStore.Load())
                targets.Add(new(device.Id, device.Distribution + " · WSL",
                    new RemoteCliManagementService((update, token) => _wslDeviceService.RunCodexHelperAsync(device, update, token))));

            var summary = new TextBlock { Text = $"기기 {targets.Count}대의 업데이트를 준비하고 있습니다.", TextWrapping = TextWrapping.Wrap };
            var rows = new StackPanel { Spacing = 20, MinWidth = 380, MaxWidth = 540 };
            rows.Children.Add(summary);
            var fields = new Dictionary<string, (TextBlock Status, TextBlock Detail, ProgressBar Progress)>();
            var states = targets.ToDictionary(target => target.Id,
                target => new CliDeviceUpdate(target.Id, CliDeviceUpdateState.Queued, "대기 중"));
            foreach (var target in targets)
            {
                var panel = new StackPanel { Spacing = 6 };
                panel.Children.Add(new TextBlock { Text = target.DisplayName, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                var status = new TextBlock { Text = "대기 중", TextWrapping = TextWrapping.Wrap };
                var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Opacity = 0.8 };
                var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
                panel.Children.Add(status);
                panel.Children.Add(detail);
                panel.Children.Add(progress);
                rows.Children.Add(panel);
                fields.Add(target.Id, (status, detail, progress));
            }
            void Render(CliDeviceUpdate update)
            {
                states[update.DeviceId] = update;
                var field = fields[update.DeviceId];
                field.Status.Text = update.State switch
                {
                    CliDeviceUpdateState.Checking => "연결·버전 확인 중",
                    CliDeviceUpdateState.Updating => "업데이트·재연결 중",
                    CliDeviceUpdateState.Completed => "완료",
                    CliDeviceUpdateState.ReconnectPending => "업데이트 완료 · 재연결 대기",
                    CliDeviceUpdateState.Failed => "실패",
                    CliDeviceUpdateState.Skipped => "확인 필요 · 건너뜀",
                    _ => "대기 중",
                };
                field.Detail.Text = update.Message;
                if (update.State == CliDeviceUpdateState.Updating && !string.IsNullOrEmpty(update.Before))
                    field.Detail.Text = "현재 버전 " + update.Before + " · " + update.Message;
                field.Progress.Visibility = update.State is CliDeviceUpdateState.Checking or CliDeviceUpdateState.Updating
                    ? Visibility.Visible : Visibility.Collapsed;
                summary.Text = $"기기 {targets.Count}대 · 완료 {states.Values.Count(state => state.State == CliDeviceUpdateState.Completed)} · "
                    + $"재연결 대기 {states.Values.Count(state => state.State == CliDeviceUpdateState.ReconnectPending)} · "
                    + $"실패 {states.Values.Count(state => state.State == CliDeviceUpdateState.Failed)} · "
                    + $"확인 필요 {states.Values.Count(state => state.State == CliDeviceUpdateState.Skipped)}";
            }
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "모든 기기 CLI 업데이트",
                Content = new ScrollViewer { MaxHeight = 480, Content = rows },
                PrimaryButtonText = "실패 기기 다시 시도", SecondaryButtonText = "닫기",
                IsPrimaryButtonEnabled = false, IsSecondaryButtonEnabled = false,
            };
            var running = true;
            var runGeneration = 0;
            dialog.Closing += (_, args) => args.Cancel = running;
            async Task RunAsync(IReadOnlyList<CliUpdateTarget> selected)
            {
                var generation = ++runGeneration;
                running = true;
                dialog.IsPrimaryButtonEnabled = false;
                dialog.IsSecondaryButtonEnabled = false;
                foreach (var target in selected) Render(new(target.Id, CliDeviceUpdateState.Queued, "작업 잠금 확인 중"));
                try
                {
                    // Acquire once for the batch, then update devices concurrently.
                    // Account switches and installations cannot overlap this operation.
                    using var mutation = await ControllerMutationLock.AcquireAsync(_paths);
                    var progress = new CliUiProgress(update => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (running && generation == runGeneration) Render(update);
                    }));
                    var results = await new CliBatchUpdater().RunAsync(selected, progress);
                    foreach (var result in results) Render(result);
                }
                catch (Exception error)
                {
                    foreach (var target in selected) Render(new(target.Id, CliDeviceUpdateState.Failed, error.Message));
                }
                finally
                {
                    running = false;
                    dialog.IsPrimaryButtonEnabled = states.Values.Any(state => state.State == CliDeviceUpdateState.Failed);
                    dialog.IsSecondaryButtonEnabled = true;
                }
            }
            dialog.Opened += async (_, _) => await RunAsync(targets);
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;
                var deferral = args.GetDeferral();
                try { await RunAsync(targets.Where(target => states[target.Id].State == CliDeviceUpdateState.Failed).ToArray()); }
                finally { deferral.Complete(); }
            };
            await dialog.ShowAsync();
            await RefreshDevicesAsync();
        }
        catch (Exception error) { SetBanner(error.Message, true); }
        finally { SetBusy(false); }
    }

    private sealed class CliUiProgress(Action<CliDeviceUpdate> report) : IProgress<CliDeviceUpdate>
    {
        public void Report(CliDeviceUpdate value) => report(value);
    }
}
