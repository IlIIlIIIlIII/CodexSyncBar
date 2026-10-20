using System.Text.Json.Nodes;
using ClaudeSyncBar.App.Controls;
using ClaudeSyncBar.App.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeSyncBar.App.Pages;

/// <summary>
/// This PC's Claude Code CLI and Claude Desktop, and the SSH devices that take
/// part in switching. A device opens onto its own page.
/// </summary>
public sealed class DevicesPage : SyncPage
{
    private JsonObject? _cli;
    private string? _cliError;
    private bool _cliLoading;

    public DevicesPage()
    {
        Loaded += (_, _) =>
        {
            _ = State.LoadDeviceListAsync();
            if (_cli is null && !_cliLoading) _ = LoadCliAsync();
        };
    }

    protected override string Signature() => base.Signature() + State.DeviceList?.ToJsonString() + _cli?.ToJsonString() + _cliError + _cliLoading;

    private async Task LoadCliAsync()
    {
        _cliLoading = true;
        Redraw();
        try
        {
            _cli = await State.Client.CallAsync("cli.status", new { deviceId = "local" }, TimeSpan.FromSeconds(60)) as JsonObject;
            _cliError = null;
        }
        catch (ServiceError error)
        {
            _cliError = error.Message;
        }
        finally
        {
            _cliLoading = false;
            Redraw();
        }
    }

    protected override void Render()
    {
        var busy = State.Busy || State.RefreshRequested;
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        var fromConfig = new MenuFlyoutItem { Text = "SSH config에서 추가", Icon = new FontIcon { Glyph = Glyphs.List } };
        fromConfig.Click += async (_, _) => await ImportSshConfigAsync();
        var manual = new MenuFlyoutItem { Text = "직접 입력", Icon = new FontIcon { Glyph = Glyphs.Add } };
        manual.Click += (_, _) => Shell.Navigate("device", new JsonObject());
        menu.Items.Add(fromConfig);
        menu.Items.Add(manual);
        var add = Ui.Button("SSH 장치 추가", Glyphs.Add, accent: true, tip: "SSH config에서 추가 · 직접 입력");
        ((StackPanel)add.Content).Children.Add(Ui.Glyph(Glyphs.ChevronDown, 10));
        add.Flyout = menu;
        add.IsEnabled = State.Error is null;
        var update = Ui.Button("모든 장치 CLI 업데이트", Glyphs.Download, click: async (_, _) =>
        {
            if (await Flows.ConfirmAsync(XamlRoot, "모든 장치의 CLI를 업데이트할까요?",
                "이 PC와 활성 SSH 장치의 Claude Code CLI를 업데이트해요. 실행 중인 세션은 다시 시작해야 새 버전을 써요.", "업데이트"))
                await State.CommandAsync("cli.updateAll", success: "CLI를 업데이트하고 있어요.");
        }, tip: "활성 장치의 Claude Code CLI를 업데이트해요. 실행 중인 세션은 다시 시작해야 새 버전을 써요.");
        update.IsEnabled = !busy && State.Error is null;
        Header("장치", "설치 및 활성화를 마친 장치만 전체 적용에 포함돼요.", add, update);
        ErrorBar();

        Group(Ubuntu.Backend.BackendPlatform.LocalDeviceName);
        var local = Rows();
        local.Children.Add(CliRow(busy));
        local.Children.Add(DesktopRow());
        local.Children.Add(Ui.Row(Ui.Columns(16, 1, Ui.KindTile(Glyphs.Folder),
            Ui.Stack(1, false, Ui.Text("Claude Code 기록과 설정"), Ui.Caption("세션 기록, 설정, MCP 서버, 메모리, 플러그인은 계정과 상관없이 모든 계정이 함께 써요.")),
            Ui.Button("폴더 열기", Glyphs.Folder, click: async (_, _) => await State.CommandAsync("open.claudeFolder")))));

        Group("SSH 장치");
        var ssh = Rows();
        foreach (var row in SshRows(busy)) ssh.Children.Add(row);
        Add(Ui.Caption("CLI를 업데이트해도 실행 중인 Claude Code 세션은 다시 시작해야 새 버전을 써요."));
    }

    private Border CliRow(bool busy)
    {
        string caption;
        if (_cliLoading && _cli is null) caption = "버전을 확인하고 있어요…";
        else if (_cliError is not null) caption = _cliError;
        else
        {
            var version = AppState.Str(_cli?["version"]);
            caption = string.Join(" · ", new[] { "Windows", string.IsNullOrEmpty(version) ? "설치되지 않음" : version, AppState.Str(_cli?["notice"]) }
                .Where(part => !string.IsNullOrEmpty(part)));
        }

        var check = Ui.IconButton(Glyphs.Refresh, "CLI 버전 다시 확인", async (_, _) => await LoadCliAsync());
        check.IsEnabled = !_cliLoading;
        var update = Ui.Button("업데이트", Glyphs.Download, click: async (_, _) =>
            await State.CommandAsync("cli.update", new { deviceId = "local" }, "CLI를 업데이트하고 있어요."));
        update.IsEnabled = !busy && QuotaFormat.Flag(_cli?["canUpdate"]);
        var text = Ui.Stack(1, false, Ui.Text("Claude Code CLI"), Ui.Caption(caption));
        return Ui.Row(Ui.Columns(16, 1, Ui.KindTile(Glyphs.Terminal), text, check, update));
    }

    private Border DesktopRow()
    {
        var local = State.Devices.FirstOrDefault(device => AppState.Str(device["id"]) == "local");
        var text = Ui.Stack(1, false, Ui.Text("Claude 데스크톱 앱"),
            Ui.Caption("계정을 바꿀 때 로그인만 바꿔요. 세션 목록, 설정, 세션 프리셋은 계정과 상관없이 그대로 이어져요."));
        if (QuotaFormat.Flag(local?["reconnectionRequired"]) && AppState.Str(local?["detail"]) is { Length: > 0 } detail)
        {
            var notice = Ui.Caption(detail);
            notice.Style = Ui.Style("SyncBarToneWarning");
            notice.FontSize = 12;
            text.Children.Add(Ui.Columns(6, 1, Ui.Glyph(Glyphs.Warning, 12, "SyncBarGlyphWarning"), notice));
        }

        var open = Ui.Button("열기", Glyphs.OpenWindow, click: async (_, _) => await State.CommandAsync("open.claude"));
        return Ui.Row(Ui.Columns(16, 1, Ui.KindTile(Glyphs.Window), text, open));
    }

    private IEnumerable<UIElement> SshRows(bool busy)
    {
        var statuses = State.Devices.Where(device => AppState.Str(device["kind"]) == "ssh").ToList();
        var configurations = State.DeviceList?.OfType<JsonObject>().ToList()
            ?? statuses.Select(device => new JsonObject
            {
                ["id"] = AppState.Str(device["id"]) is { } id && id.StartsWith("ssh:") ? id[4..] : AppState.Str(device["id"]),
                ["displayName"] = AppState.Str(device["displayName"]), ["enabled"] = QuotaFormat.Flag(device["enabled"]),
            }).ToList();
        if (configurations.Count == 0)
        {
            yield return Ui.Card(Ui.Text(State.DeviceList is null ? "장치 목록을 불러오고 있어요…"
                : "등록된 SSH 장치가 없어요. SSH config에서 추가하거나 직접 입력해 주세요.", secondary: true));
            yield break;
        }

        foreach (var configuration in configurations)
        {
            var id = AppState.Str(configuration["id"]) ?? "";
            var status = statuses.FirstOrDefault(device => AppState.Str(device["id"]) == "ssh:" + id);
            yield return SshRow(configuration, status);
        }
    }

    private UIElement SshRow(JsonObject configuration, JsonObject? status)
    {
        var id = AppState.Str(configuration["id"]) ?? "";
        var host = AppState.Str(configuration["host"]);
        var name = AppState.Str(configuration["displayName"]) is { Length: > 0 } display ? display : host ?? "SSH 장치";
        var enabled = QuotaFormat.Flag(configuration["enabled"]);
        var reachable = QuotaFormat.Flag(status?["isReachable"]);
        var unreachable = AppState.Str(status?["status"]) == "unreachable";
        var parts = new List<string>();
        if (host is not null) parts.Add($"{AppState.Str(configuration["username"])}@{host}:{configuration["port"]?.ToJsonString() ?? "22"}");
        parts.Add(!enabled ? "설치 및 활성화 전" : reachable ? "연결됨" : unreachable ? "연결 실패" : "확인 필요");
        if (reachable && AppState.Str(status?["currentAccountLabel"]) is { Length: > 0 } label) parts.Add("지금 " + label);
        if (QuotaFormat.Flag(configuration["skipOnSwitchFailure"])) parts.Add("전환 실패 시 건너뜀");
        var pill = !enabled ? Ui.Pill("전체 적용에서 빠짐", Ui.PillKind.Neutral)
            : unreachable ? Ui.Pill("연결 실패", Ui.PillKind.Caution, Glyphs.Warning)
            : Ui.Pill("전체 적용에 포함", Ui.PillKind.Success, Glyphs.Check);
        var text = Ui.Stack(1, false, Ui.Text(name, wrap: false), Ui.Caption(string.Join(" · ", parts)));
        var chevron = Ui.Glyph(Glyphs.ChevronRight, 12, "SyncBarGlyphMuted");
        var button = new Button
        {
            Content = Ui.Columns(16, 1, Ui.KindTile(Glyphs.Server), text, pill, chevron),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(4),
        };
        AutomationProperties.SetName(button, $"{name}, {string.Join(", ", parts)}");
        ToolTipService.SetToolTip(button, "장치 설정 열기");
        button.Click += (_, _) => Shell.Navigate("device", id);
        return button;
    }

    // Lists the aliases of ~/.ssh/config; the chosen one becomes a new device to check and save.
    private async Task ImportSshConfigAsync()
    {
        List<string> hosts;
        try { hosts = SshConfig.Hosts(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            State.Say(error is FormatException ? error.Message : "SSH config를 읽지 못했어요.", NoticeKind.Warning);
            return;
        }

        var registered = State.DeviceList?.OfType<JsonObject>().Select(device => AppState.Str(device["host"])?.ToLowerInvariant()).ToHashSet() ?? [];
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 320 };
        AutomationProperties.SetName(list, "SSH 호스트");
        foreach (var host in hosts)
        {
            var exists = registered.Contains(host.ToLowerInvariant());
            list.Items.Add(new ListViewItem
            {
                Content = Ui.Stack(0, false, Ui.Text(host, wrap: false), Ui.Caption(exists ? "이미 등록된 호스트예요" : "OpenSSH 설정 사용", wrap: false)),
                Tag = host, IsEnabled = !exists,
            });
        }

        var content = Ui.Stack(12, false, Ui.Caption("~/.ssh/config의 호스트를 골라 주세요. 연결 설정을 확인한 뒤 저장할 수 있어요."));
        content.Children.Add(hosts.Count == 0
            ? Ui.Text("추가할 SSH 호스트가 없어요. ~/.ssh/config에 Host 별칭을 추가해 주세요. 와일드카드 항목은 보이지 않아요.", secondary: true)
            : list);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "SSH config에서 추가", Content = content, PrimaryButtonText = "다음", CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false, RequestedTheme = ActualTheme,
        };
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is ListViewItem;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedItem is not ListViewItem { Tag: string alias }) return;
        try
        {
            var draft = await Task.Run(() => SshConfig.Resolve(alias));
            Shell.Navigate("device", draft);
            State.Say("설정을 확인한 뒤 저장해 주세요. 저장하면 설치 및 활성화를 할 수 있어요.");
        }
        catch (FormatException error)
        {
            State.Say(error.Message, NoticeKind.Warning);
        }
    }
}
