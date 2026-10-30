using System.Text.Json.Nodes;
using ClaudeSyncBar.App.Controls;
using ClaudeSyncBar.App.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeSyncBar.App;

/// <summary>Actions shared by the window and the tray: they confirm in a dialog before anything changes.</summary>
public static class Flows
{
    private static AppState State => App.Current!.State;
    private static bool _dialogOpen;

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string body, string action, bool destructive = false)
    {
        if (_dialogOpen) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action, CloseButtonText = "취소",
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };
        _dialogOpen = true;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { _dialogOpen = false; }
    }

    /// <summary>The path of a file the user picks, or null when they cancel.</summary>
    public static async Task<string?> PickFileAsync(params string[] types)
    {
        var picker = new global::Windows.Storage.Pickers.FileOpenPicker();
        // An unpackaged app names the window the picker belongs to.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Current!.Window!));
        foreach (var type in types.Append("*")) picker.FileTypeFilter.Add(type);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>
    /// The smart switch: among the accounts ticked on the account list with
    /// some of both the weekly and the five-hour quota left, the one whose week
    /// resets first, applied to every active device after a confirmation.
    /// </summary>
    public static async Task SmartSwitchAsync(XamlRoot root)
    {
        if (State.Busy || State.RefreshRequested || _dialogOpen) return;
        JsonNode? result;
        try
        {
            // Usage older than two minutes is looked up first, which can take a moment.
            result = await State.Client.CallAsync("smart.preview", timeout: TimeSpan.FromSeconds(90));
        }
        catch (ServiceError error)
        {
            State.Say(error.Message, NoticeKind.Error);
            return;
        }

        if (result?["preview"] is not JsonObject preview)
        {
            State.Say(AppState.Str(result?["message"]) ?? "전환할 계정을 찾지 못했어요.", NoticeKind.Info);
            return;
        }

        if (!QuotaFormat.Flag(preview["canApply"]))
        {
            State.Say(AppState.Str(preview["blockingReason"]) ?? "지금은 전환할 수 없어요.", NoticeKind.Warning);
            return;
        }

        var target = State.Lookup(AppState.Int(result["profileId"]));
        var label = AppState.Str(result["accountLabel"]) ?? AppState.AccountName(target);
        var content = new StackPanel { Spacing = 14, MaxWidth = 440 };
        var from = State.Active is { } active ? AppState.AccountName(active) + "에서 " : "";
        content.Children.Add(Ui.Text($"{from}{label}(으)로 모든 활성 장치를 전환할까요?"));
        if (target is not null) content.Children.Add(TargetSummary(target));
        content.Children.Add(Ui.Caption($"스마트 전환 후보 중 주간 한도가 남고 5시간 한도를 75% 미만 쓴 계정에서 주간 초기화가 가장 가까워요. ({AppState.Str(result["reason"])})"));
        if (AppState.Str(preview["desktopNotice"]) is { } notice)
        {
            var row = Ui.Stack(10, true, Ui.Glyph(Glyphs.Info, 14, "SyncBarGlyphMuted"), Ui.Caption(notice));
            ((FrameworkElement)row.Children[1]).MaxWidth = 400;
            content.Children.Add(row);
        }

        if (_dialogOpen) return;
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = "스마트 전환", Content = content, PrimaryButtonText = "전환", CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };
        _dialogOpen = true;
        ContentDialogResult answer;
        try { answer = await dialog.ShowAsync(); }
        finally { _dialogOpen = false; }
        if (answer == ContentDialogResult.Primary) await State.ApplyPreviewAsync(preview);
    }

    private static Border TargetSummary(JsonObject account)
    {
        var name = AppState.AccountName(account);
        var identity = new Grid { ColumnSpacing = 12 };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identity.Children.Add(Ui.Avatar(AppState.Int(account["id"]), name, 32));
        var details = string.Join(" · ", new[] { AppState.Str(account["email"]), AppState.Str(account["plan"]) }.Where(value => !string.IsNullOrEmpty(value) && value != name));
        var names = Ui.Stack(0, false, Ui.Text(name, strong: true), Ui.Caption(details));
        Grid.SetColumn(names, 1);
        identity.Children.Add(names);
        var quotas = new Grid { ColumnSpacing = 20 };
        var usage = QuotaFormat.Account(account, State.VisibleQuotas, DateTimeOffset.UtcNow);
        var column = 0;
        foreach (var quota in usage.Quotas)
        {
            quotas.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var value = Ui.Stack(6, true, Ui.ToneText(quota.Value, quota.Tone, 20), Ui.Caption("남음"));
            ((FrameworkElement)value.Children[1]).VerticalAlignment = VerticalAlignment.Bottom;
            var cell = Ui.Stack(4, false, Ui.Caption(quota.Title), value, Ui.Meter(quota));
            Grid.SetColumn(cell, column++);
            quotas.Children.Add(cell);
        }

        return new Border
        {
            Style = Ui.Style("SyncBarSubRowStyle"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(16, 14, 16, 14), Child = Ui.Stack(12, false, identity, quotas),
        };
    }
}
