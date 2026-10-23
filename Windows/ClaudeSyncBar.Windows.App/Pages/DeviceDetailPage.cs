using System.Text.Json.Nodes;
using ClaudeSyncBar.App.Controls;
using ClaudeSyncBar.App.Model;
using ClaudeSyncBar.Windows.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeSyncBar.App.Pages;

/// <summary>
/// One SSH device: its state and checks, its connection and authentication,
/// and whether switching includes it. The form is saved with its button; what
/// is typed survives the page being drawn again.
/// </summary>
public sealed class DeviceDetailPage : SyncPage
{
    private static readonly (string Value, string Label)[] Methods =
        [("openSSHConfig", "OpenSSH 설정 사용"), ("privateKey", "개인 키"), ("password", "비밀번호")];
    // ssh.exe asks this helper for a password or key passphrase; without it only keys without one work.
    private static readonly Lazy<bool> HasAskPass = new(() => File.Exists(new WindowsPaths().BundledWindowsAskPass));
    private readonly string? _id;
    private JsonObject? _draft;
    private string _password = "";
    private string _passphrase = "";
    private bool _clearPassphrase;

    /// <param name="parameter">A device id, or a new device's values to start from.</param>
    public DeviceDetailPage(object? parameter)
    {
        if (parameter is JsonObject draft) _draft = draft;
        else _id = parameter as string;
        Loaded += (_, _) => { if (_id is not null) _ = State.LoadDeviceListAsync(); };
    }

    private JsonObject? Status => _id is null ? null : State.Devices.FirstOrDefault(device => AppState.Str(device["id"]) == "ssh:" + _id);
    private JsonObject? Saved => State.DeviceConfiguration(_id);

    // Only what this page shows; usage polls never redraw the form under the cursor.
    protected override string Signature() => string.Join("|", State.Busy, State.Error, AppState.Str(State.Operation?["state"]),
        Status?.ToJsonString(), Saved?.ToJsonString(), State.DeviceList is null, State.SelectedProfileId,
        State.Selected is { } selected ? AppState.AccountName(selected) + AppState.NeedsLogin(selected) : "", _draft is null);

    private string Value(string key) => _draft?[key] is JsonValue value
        ? value.TryGetValue<string>(out var text) ? text : value.ToJsonString()
        : "";

    protected override void Render()
    {
        if (_draft is null && Saved is { } saved) _draft = (JsonObject)saved.DeepClone();
        var name = Value("displayName") is { Length: > 0 } display ? display : Value("host") is { Length: > 0 } host ? host
            : _id is null ? "새 SSH 장치" : "SSH 장치";
        Crumb(_draft is null && _id is not null ? "SSH 장치" : name);
        if (_draft is null)
        {
            Add(Ui.Card(Ui.Text(State.DeviceList is null ? "장치 설정을 불러오고 있어요…" : "이 장치를 찾지 못했어요. 장치 목록에서 다시 골라 주세요.", secondary: true)));
            return;
        }

        ErrorBar();
        if (_id is not null) Add(StatusCard(name));
        Add(ConnectionCard());
        Add(AuthenticationCard());
        var options = Rows();
        var activated = QuotaFormat.Flag(Saved?["enabled"]);
        options.Children.Add(Ui.SettingRow(null, "전체 적용에 포함",
            activated ? "모든 장치에 적용할 때 이 장치도 함께 바꿔요." : "설치 및 활성화를 마치면 포함할 수 있어요.",
            Ui.Toggle(QuotaFormat.Flag(_draft["enabled"]), "전체 적용에 포함", on => _draft["enabled"] = on, enabled: activated)));
        options.Children.Add(Ui.SettingRow(null, "계정 전환 실패 시 건너뛰기", "연결이나 전환에 실패해도 다른 장치의 전환을 계속해요.",
            Ui.Toggle(QuotaFormat.Flag(_draft["skipOnSwitchFailure"]), "계정 전환 실패 시 건너뛰기", on => _draft["skipOnSwitchFailure"] = on)));
        Add(Actions());
    }

    // "장치 › 개발 서버", the way Windows Settings titles a page under another.
    private void Crumb(string name)
    {
        var bar = new BreadcrumbBar { ItemsSource = new[] { "장치", name } };
        bar.Resources["BreadcrumbBarItemThemeFontSize"] = 28.0;
        bar.Resources["BreadcrumbBarChevronFontSize"] = 16.0;
        bar.Resources["BreadcrumbBarItemFontWeight"] = FontWeights.SemiBold;
        bar.ItemClicked += (_, args) => { if (args.Index == 0) Shell.Navigate("devices"); };
        AutomationProperties.SetName(bar, "위치");
        Add(bar);
    }

    private Border StatusCard(string name)
    {
        var status = Status;
        var activated = QuotaFormat.Flag(Saved?["enabled"]);
        var reachable = QuotaFormat.Flag(status?["isReachable"]);
        var unreachable = AppState.Str(status?["status"]) == "unreachable";
        var pill = !activated ? Ui.Pill("설치 및 활성화 전", Ui.PillKind.Neutral)
            : reachable ? Ui.Pill("연결됨", Ui.PillKind.Success, Glyphs.Check)
            : unreachable ? Ui.Pill("연결 실패", Ui.PillKind.Caution, Glyphs.Warning)
            : Ui.Pill("확인 필요", Ui.PillKind.Neutral);
        var parts = new List<string> { activated ? "활성화 완료" : "호스트 키를 확인하고 설치해야 전환에 포함돼요" };
        if (AppState.Str(status?["detail"]) is { Length: > 0 } detail) parts.Add(detail);
        if (reachable && AppState.Str(status?["currentAccountLabel"]) is { Length: > 0 } label) parts.Add("지금 " + label);
        var title = Ui.Stack(8, true, Ui.Text(name, strong: true, wrap: false), pill);
        var text = Ui.Stack(2, false, title, Ui.Caption(string.Join(" · ", parts)));
        var busy = State.Busy;
        var test = Ui.Button("연결 테스트", click: async (_, _) => await TestAsync());
        var trust = Ui.Button("호스트 키 확인", MoreGlyphs.Fingerprint, click: async (_, _) => await TrustAsync(install: false));
        var install = Ui.Button(activated ? "다시 설치" : "설치 및 활성화", Glyphs.Download, accent: !activated, click: async (_, _) => await TrustAsync(install: true),
            tip: "호스트 키를 확인하고 원격 장치에 전환 도구를 설치해요");
        test.IsEnabled = trust.IsEnabled = install.IsEnabled = !busy && State.Error is null;
        // Beside the name when there is room for all three, under it otherwise.
        var body = Wide
            ? (UIElement)Ui.Columns(16, 1, Ui.KindTile(Glyphs.Server), text, Ui.Stack(8, true, test, trust, install))
            : Ui.Stack(12, false, Ui.Columns(16, 1, Ui.KindTile(Glyphs.Server), text), Ui.Wrap(8, test, trust, install));
        return Ui.Card(body);
    }

    private TextBox Field(string label, string key, string? placeholder = null)
    {
        var box = new TextBox { Header = label, Text = Value(key), PlaceholderText = placeholder ?? "" };
        box.TextChanged += (_, _) => _draft![key] = box.Text;
        return box;
    }

    private Border ConnectionCard()
    {
        var fields = new UIElement[]
        {
            Field("표시 이름", "displayName", "목록에 보일 이름"),
            Field("호스트", "host", "주소 또는 SSH config 별칭"),
            Field("포트", "port", "22"),
            Field("사용자 이름", "username"),
        };
        ((TextBox)fields[2]).InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.Number) } };
        return Ui.Card(Ui.Stack(12, false, Ui.Text("연결", strong: true), Fields(fields)));
    }

    // Two fields to a line when there is room, one otherwise.
    private Grid Fields(IReadOnlyList<UIElement> fields)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 12 };
        var columns = Wide ? 2 : 1;
        for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < fields.Count; i++)
        {
            if (i % columns == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var element = (FrameworkElement)fields[i];
            Grid.SetRow(element, i / columns);
            Grid.SetColumn(element, i % columns);
            grid.Children.Add(element);
        }

        return grid;
    }

    private Border AuthenticationCard()
    {
        var mode = Value("authentication") is { Length: > 0 } chosen ? chosen : "openSSHConfig";
        var picker = new ComboBox
        {
            Header = "인증 방식", ItemsSource = Methods.Select(method => method.Label).ToList(),
            SelectedIndex = Math.Max(0, Array.FindIndex(Methods, method => method.Value == mode)), MinWidth = 220,
        };
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex < 0 || Methods[picker.SelectedIndex].Value == mode) return;
            _draft!["authentication"] = Methods[picker.SelectedIndex].Value;
            Redraw();
        };
        var body = Ui.Stack(12, false, Ui.Text("인증", strong: true), picker);
        var saved = Saved;
        switch (mode)
        {
            case "privateKey":
            {
                var key = Field("개인 키 파일", "identityFile", @"예: C:\Users\me\.ssh\id_ed25519");
                var browse = Ui.IconButton(MoreGlyphs.OpenFile, "개인 키 파일 찾아보기", async (_, _) =>
                {
                    if (await Flows.PickFileAsync() is not { } path) return;
                    _draft!["identityFile"] = path;
                    Redraw();
                }, subtle: false);
                browse.VerticalAlignment = VerticalAlignment.Bottom;
                var keyRow = Ui.Columns(8, 0, key, browse);
                var passphrase = Secret("새 키 암호", QuotaFormat.Flag(saved?["hasKeyPassphrase"]) ? "비워 두면 기존 값 유지" : "암호가 없는 키는 비워 두세요",
                    _passphrase, value => _passphrase = value);
                body.Children.Add(Fields([keyRow, Field("SSH 인증서 (선택)", "certificateFile", "없음"), passphrase]));
                if (QuotaFormat.Flag(saved?["hasKeyPassphrase"]))
                    body.Children.Add(Ui.Columns(12, 0, Ui.Stack(0, false, Ui.Text("저장된 키 암호 지우기"), Ui.Caption("암호가 없는 키로 바꿀 때 써요.")),
                        Ui.Toggle(_clearPassphrase, "저장된 키 암호 지우기", on => _clearPassphrase = on)));
                if (!HasAskPass.Value) body.Children.Add(Unsupported("키 암호는 아직 Windows에서 쓸 수 없어요. 암호가 없는 키나 ssh-agent에 등록한 키를 써 주세요."));
                break;
            }
            case "password":
                body.Children.Add(Fields([Secret("새 비밀번호", QuotaFormat.Flag(saved?["hasPassword"]) ? "비워 두면 기존 값 유지" : "", _password, value => _password = value)]));
                if (!HasAskPass.Value) body.Children.Add(Unsupported("비밀번호 인증은 아직 Windows에서 쓸 수 없어요. OpenSSH 설정이나 키 인증을 써 주세요."));
                break;
            default:
                body.Children.Add(Ui.Caption("~/.ssh/config의 설정과 ssh-agent에 등록한 키를 그대로 써요. 호스트에 SSH config 별칭을 적을 수 있어요."));
                break;
        }

        return Ui.Card(body);
    }

    private static PasswordBox Secret(string label, string placeholder, string value, Action<string> changed)
    {
        var box = new PasswordBox { Header = label, PlaceholderText = placeholder, Password = value };
        box.PasswordChanged += (_, _) => changed(box.Password);
        return box;
    }

    private static InfoBar Unsupported(string message) => new()
    {
        IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = message,
    };

    private Grid Actions()
    {
        var busy = State.Busy;
        var save = Keep("save", () => Ui.Button("저장", accent: true, click: async (_, _) => await SaveAsync()));
        save.IsEnabled = !busy && State.Error is null;
        Button? only = null;
        if (_id is not null && QuotaFormat.Flag(Saved?["enabled"]) && State.Selected is { } selected && !AppState.NeedsLogin(selected))
        {
            only = Ui.Button($"이 장치에만 {AppState.AccountName(selected)} 적용", click: async (_, _) => await ApplyHereAsync(selected),
                tip: "계정 전환에서 고른 계정을 이 장치에만 적용해요");
            only.IsEnabled = !busy && State.Error is null;
        }

        Button? delete = null;
        if (_id is not null)
        {
            delete = Ui.Button("장치 삭제", Glyphs.Delete, click: async (_, _) => await DeleteAsync());
            delete.Style = Ui.Style("SubtleButtonStyle");
            delete.IsEnabled = !busy;
        }

        var grid = Ui.Columns(8, 2, save, only, new Border(), delete);
        grid.Margin = new Thickness(0, 4, 0, 0);
        return grid;
    }

    private async Task SaveAsync()
    {
        if (!int.TryParse(Value("port").Trim(), out var port) || port is < 1 or > 65535)
        {
            State.Say("포트는 1~65535 사이의 숫자여야 해요.", NoticeKind.Warning);
            return;
        }

        if (Value("host").Trim().Length == 0)
        {
            State.Say("호스트를 입력해 주세요.", NoticeKind.Warning);
            return;
        }

        string? Optional(string key) => Value(key).Trim() is { Length: > 0 } text ? text : null;
        var device = new JsonObject
        {
            ["displayName"] = Value("displayName").Trim(), ["host"] = Value("host").Trim(), ["port"] = port, ["username"] = Value("username").Trim(),
            ["authentication"] = Value("authentication") is { Length: > 0 } mode ? mode : "openSSHConfig",
            ["identityFile"] = Optional("identityFile"), ["certificateFile"] = Optional("certificateFile"),
            ["enabled"] = QuotaFormat.Flag(_draft!["enabled"]), ["skipOnSwitchFailure"] = QuotaFormat.Flag(_draft["skipOnSwitchFailure"]),
        };
        if (_id is not null) device["id"] = _id;
        var parameters = new JsonObject { ["device"] = device, ["clearPassphrase"] = _clearPassphrase };
        if (_password.Length > 0) parameters["password"] = _password;
        if (_passphrase.Length > 0) parameters["passphrase"] = _passphrase;
        if (await State.CommandAsync("device.save", parameters, "장치 설정을 저장했어요.") is not { } saved) return;
        _password = _passphrase = "";
        _clearPassphrase = false;
        await State.LoadDeviceListAsync();
        if (_id is null && AppState.Str(saved["id"]) is { } created)
        {
            Shell.Navigate("device", created);
            return;
        }

        _draft = null;
        Redraw();
    }

    private async Task TestAsync()
    {
        if (await State.CommandAsync("device.test", new { deviceId = _id }) is not { } result) return;
        if (result["hostKey"] is JsonObject)
        {
            await TrustAsync(install: false);
            return;
        }

        State.Say(AppState.Str(result["message"]) ?? "연결을 확인했어요.", QuotaFormat.Flag(result["isReachable"]) ? NoticeKind.Success : NoticeKind.Warning);
    }

    // The server's key is shown for the user to compare before anything is sent to it; then the device may be installed.
    private async Task TrustAsync(bool install)
    {
        if (await State.CommandAsync("device.trust", new { deviceId = _id }) is not { } result) return;
        if (!QuotaFormat.Flag(result["trusted"]))
        {
            if (result["hostKey"] is not JsonObject key)
            {
                State.Say("호스트 키를 확인하지 못했어요.", NoticeKind.Warning);
                return;
            }

            var body = $"{AppState.Str(key["host"])}:{key["port"]?.ToJsonString()}\n\n{AppState.Str(key["fingerprint"])}\n\n서버 관리자가 알려 준 지문과 같은지 비교해 주세요.";
            if (!await Flows.ConfirmAsync(XamlRoot, "이 서버의 호스트 키를 신뢰할까요?", body, "신뢰하고 등록")) return;
            if (await State.CommandAsync("device.trust", new JsonObject { ["deviceId"] = _id, ["approvedKey"] = key.DeepClone() },
                install ? null : "호스트 키를 등록했어요.") is null) return;
        }
        else if (!install)
        {
            State.Say("등록된 호스트 키와 일치해요.", NoticeKind.Success);
            return;
        }

        if (install) await State.CommandAsync("device.bootstrap", new { deviceId = _id }, "장치를 설치하고 활성화하고 있어요.");
    }

    private async Task ApplyHereAsync(JsonObject account)
    {
        JsonObject? preview;
        try { preview = await State.Client.CallAsync("preview", new { profileId = AppState.Int(account["id"]) ?? 0, deviceId = _id }) as JsonObject; }
        catch (ServiceError error)
        {
            State.Say(error.Message, NoticeKind.Error);
            return;
        }

        if (preview is null) return;
        if (!QuotaFormat.Flag(preview["canApply"]))
        {
            State.Say(AppState.Str(preview["blockingReason"]) ?? "지금은 적용할 수 없어요.", NoticeKind.Warning);
            return;
        }

        if (await Flows.ConfirmAsync(XamlRoot, "이 SSH 장치에 적용할까요?", $"{AppState.AccountName(account)}을(를) 이 장치에만 적용해요. 다른 장치는 바뀌지 않아요.", "적용"))
            await State.ApplyPreviewAsync(preview);
    }

    private async Task DeleteAsync()
    {
        if (!await Flows.ConfirmAsync(XamlRoot, "SSH 장치를 삭제할까요?", "목록에서 지우고 저장한 비밀번호와 키 암호도 지워요. 원격 장치의 계정은 바꾸지 않아요.",
            "삭제", destructive: true)) return;
        if (await State.CommandAsync("device.delete", new { deviceId = _id }, "장치를 삭제했어요.") is null) return;
        await State.LoadDeviceListAsync();
        Shell.Navigate("devices");
    }
}
