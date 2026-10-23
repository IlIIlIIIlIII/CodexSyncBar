using System.Text.Json.Nodes;
using ClaudeSyncBar.App.Controls;
using ClaudeSyncBar.App.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeSyncBar.App.Pages;

/// <summary>
/// The registered accounts in priority order, each opening onto its alias,
/// place in the order, sign-in and logout. Nothing here changes the account in use.
/// </summary>
public sealed class AccountsPage : SyncPage
{
    // Which accounts are open, kept while the app runs so coming back finds them as left.
    // The first visit opens the account in use.
    private static readonly HashSet<int> Open = [];
    private static bool _visited;
    // Aliases being typed, so a redraw never loses them.
    private readonly Dictionary<int, string> _aliases = [];

    protected override void Render()
    {
        var busy = State.Busy || State.RefreshRequested;
        var add = Keep("add", () => Ui.Button("계정 추가", Glyphs.Add, accent: true, click: async (_, _) =>
            await State.CommandAsync("account.add", success: "브라우저에서 로그인을 마쳐 주세요.")));
        add.IsEnabled = !busy && State.Error is null;
        var import = Ui.Button("인증 파일 가져오기", Glyphs.Import, click: async (_, _) => await ImportAsync(),
            tip: "Claude 인증 파일(.credentials.json) 가져오기");
        import.IsEnabled = !busy && State.Error is null;
        var refresh = Ui.IconButton(Glyphs.Refresh, "목록 새로고침", async (_, _) => await State.PollAsync(), subtle: false);
        Header("계정", "계정 순서는 자동 전환 우선순위와 같아요. 계정을 고르기만 해서는 적용되지 않아요.", add, import, refresh);
        ErrorBar();
        if (LoginBar() is { } login) Add(login);

        var accounts = State.Accounts;
        if (accounts.Count == 0)
        {
            Add(Ui.Card(Ui.Text("등록된 계정이 없어요. 계정을 추가하거나 인증 파일을 가져와 주세요.", secondary: true)));
            return;
        }

        if (!_visited && State.ActiveProfileId is { } active)
        {
            _visited = true;
            Open.Add(active);
        }

        var list = new StackPanel { Spacing = 4 };
        AutomationProperties.SetName(list, "등록된 계정");
        for (var i = 0; i < accounts.Count; i++) list.Children.Add(Account(accounts[i], i, accounts.Count, busy));
        Add(list);
        Add(Ui.Caption("로그인한 계정은 사용량을 따로 보지만, 세션 기록과 설정은 모든 계정이 함께 써요. 계정을 바꿔도 작업이 그대로 이어져요."));
    }

    private static async Task ImportAsync()
    {
        if (await Flows.PickFileAsync(".json") is not { } path) return;
        await State.CommandAsync("account.import", new { path }, "인증 파일을 가져오고 있어요.");
    }

    // While the browser sign-in runs, how to bring its window back or stop it.
    private static InfoBar? LoginBar()
    {
        if (!State.Busy || AppState.Str(State.Operation?["kind"]) != "login") return null;
        var id = AppState.Int(State.Operation?["profileId"]);
        var account = State.Lookup(id);
        var name = account is null || QuotaFormat.Flag(account["isPending"]) ? "새 계정" : AppState.AccountName(account);
        var actions = Ui.Stack(8, true,
            Ui.Button("로그인 창 다시 열기", click: async (_, _) => await State.CommandAsync("account.reopen", new { profileId = id ?? 0 })),
            Ui.Button("취소", click: async (_, _) => await State.CommandAsync("account.cancel")));
        actions.Margin = new Thickness(0, 0, 0, 12);
        return new InfoBar
        {
            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
            Title = $"{name} 로그인을 기다리고 있어요", Message = "브라우저에서 로그인을 마치면 자동으로 이어져요.", Content = actions,
        };
    }

    private UIElement Account(JsonObject account, int index, int count, bool busy)
    {
        var id = AppState.Int(account["id"]) ?? 0;
        var name = AppState.AccountName(account);
        var current = id == State.ActiveProfileId;
        var open = Open.Contains(id);
        var waiting = State.Busy && AppState.Str(State.Operation?["kind"]) == "login" && AppState.Int(State.Operation?["profileId"]) == id;
        var badge = current ? Ui.Pill("사용 중", Ui.PillKind.Accent, Glyphs.Check) : null;
        UIElement status = waiting
            ? Ui.Stack(6, true, Keep("loginRing", () => new ProgressRing { IsActive = true, Width = 12, Height = 12 }), Ui.Caption("로그인 대기 중", wrap: false))
            : AppState.NeedsLogin(account)
                ? Ui.Pill(QuotaFormat.Flag(account["isPending"]) ? "로그인 전" : "재로그인 필요", Ui.PillKind.Caution, Glyphs.Lock)
                : Ui.Status("인증 정상", Tone.Healthy, Glyphs.Completed);
        var chevron = Ui.ExpandButton(open, name, () =>
        {
            if (!Open.Remove(id)) Open.Add(id);
            Redraw();
        });
        var header = Ui.Row(Ui.Columns(12, 0, Ui.Identity(account, 40, badge: badge), status, chevron), current);
        header.MinHeight = 60;
        header.Padding = new Thickness(16, 10, 16, 10);
        AutomationProperties.SetName(header, $"{index + 1}번째, {name}");
        return open ? Ui.Expander(header, Details(account, id, name, index, count, busy)) : header;
    }

    private List<UIElement> Details(JsonObject account, int id, string name, int index, int count, bool busy)
    {
        var rows = new List<UIElement>();
        var email = AppState.Str(account["email"]) ?? "";
        var alias = AppState.Str(account["alias"]) is { } kept && kept != email ? kept : "";
        var box = new TextBox { Text = _aliases.GetValueOrDefault(id, alias), MaxLength = 5, Width = 220, PlaceholderText = "최대 5자" };
        AutomationProperties.SetName(box, $"{name} 별칭");
        box.TextChanged += (_, _) => _aliases[id] = box.Text;
        var save = Ui.Button("저장", click: async (_, _) =>
        {
            if (await State.CommandAsync("account.rename", new { profileId = id, alias = box.Text.Trim() }, "별칭을 저장했어요.") is not null)
                _aliases.Remove(id);
        });
        save.IsEnabled = !busy;
        rows.Add(Ui.SubRow("별칭", Ui.Wrap(8, box, save)));

        var up = Ui.Button("위로", Glyphs.Up, click: async (_, _) => await State.CommandAsync("account.move", new { profileId = id, offset = -1 }));
        up.IsEnabled = !busy && index > 0;
        var down = Ui.Button("아래로", Glyphs.Down, click: async (_, _) => await State.CommandAsync("account.move", new { profileId = id, offset = 1 }));
        down.IsEnabled = !busy && index < count - 1;
        rows.Add(Ui.SubRow("순서", Ui.Wrap(8, up, down, Ui.Caption($"{index + 1}번째 · 자동 전환은 위 계정부터 골라요", wrap: false))));

        var signedIn = !AppState.NeedsLogin(account);
        var refresh = Ui.Button("인증 갱신", click: async (_, _) => await State.CommandAsync("auth.refresh", new { profileId = id }),
            tip: "로그인 토큰을 새로 받아요");
        refresh.IsEnabled = !busy && signedIn;
        var sync = Ui.Button("인증 동기화", click: async (_, _) => await State.CommandAsync("auth.sync", new { profileId = id }),
            tip: "이 계정의 최신 인증을 모든 활성 장치에 맞춰요");
        sync.IsEnabled = !busy && signedIn;
        rows.Add(Ui.SubRow("인증", Ui.Wrap(8, refresh, sync, LoginMenu(id, busy))));

        var others = State.Accounts.Where(other => AppState.Int(other["id"]) != id && !AppState.NeedsLogin(other)).ToList();
        var logout = new List<UIElement?>();
        if (signedIn && others.Count > 0)
        {
            var fallback = new ComboBox
            {
                ItemsSource = others.Select(AppState.AccountName).ToList(), SelectedIndex = 0, Width = 160,
            };
            AutomationProperties.SetName(fallback, "로그아웃한 뒤 대신 적용할 계정");
            var button = Ui.Button("로그아웃", click: async (_, _) =>
            {
                var target = others[Math.Max(0, fallback.SelectedIndex)];
                if (!await Flows.ConfirmAsync(XamlRoot, "이 계정에서 로그아웃할까요?",
                    $"이 계정을 쓰는 장치에는 {AppState.AccountName(target)}을(를) 적용한 뒤 보관한 인증을 지워요.", "로그아웃", destructive: true)) return;
                await State.CommandAsync("account.logout", new { profileId = id, fallbackProfileId = AppState.Int(target["id"]) ?? 0 });
            });
            button.IsEnabled = !busy;
            logout.AddRange([Ui.Caption("대신 적용할 계정", wrap: false), fallback, button]);
        }
        else if (signedIn)
        {
            logout.Add(Ui.Caption("로그아웃하려면 로그인된 다른 계정이 필요해요."));
        }

        if (!signedIn)
        {
            var delete = Ui.Button("계정 삭제", Glyphs.Delete, click: async (_, _) =>
            {
                if (!await Flows.ConfirmAsync(XamlRoot, "계정을 삭제할까요?", "목록에서 지우고 이 계정의 보관 정보를 제거해요. 장치에 적용된 계정은 바뀌지 않아요.",
                    "삭제", destructive: true)) return;
                Open.Remove(id);
                await State.CommandAsync("account.delete", new { profileId = id }, "계정을 삭제했어요.");
            });
            delete.IsEnabled = !busy;
            logout.Add(delete);
        }

        rows.Add(Ui.SubRow(signedIn ? "로그아웃" : "삭제", Ui.Wrap(8, [.. logout])));
        return rows;
    }

    // Signing in again, in the account's own browser profile or a fresh one, or with another account.
    private DropDownButton LoginMenu(int id, bool busy)
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        void Item(string text, string? tip, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = text };
            if (tip is not null) ToolTipService.SetToolTip(item, tip);
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
        }

        Item("로그인", "이 계정의 브라우저에서 다시 로그인해요", () => State.CommandAsync("account.login", new { profileId = id }, "브라우저에서 로그인을 마쳐 주세요."));
        Item("새 브라우저로 로그인", "브라우저 로그인 상태를 지우고 처음부터 로그인해요", async () =>
        {
            if (await Flows.ConfirmAsync(XamlRoot, "브라우저 로그인 상태를 초기화할까요?", "이 계정의 전용 브라우저 프로필을 초기화한 뒤 다시 로그인해요.", "새로 로그인"))
                await State.CommandAsync("account.login", new { profileId = id, fresh = true }, "브라우저에서 로그인을 마쳐 주세요.");
        });
        Item("다른 계정으로 인증 교체", "로그인하는 계정으로 이 항목의 인증을 바꿔요", async () =>
        {
            if (await Flows.ConfirmAsync(XamlRoot, "보관한 인증을 바꿀까요?", "로그인하는 계정으로 이 항목의 인증을 바꿔요. 기존 인증은 로그인에 성공할 때까지 남겨 둬요.", "로그인 시작"))
                await State.CommandAsync("account.login", new { profileId = id, fresh = true, replaceExisting = true }, "브라우저에서 로그인을 마쳐 주세요.");
        });
        var button = new DropDownButton { Content = "다시 로그인", Flyout = menu, IsEnabled = !busy };
        AutomationProperties.SetName(button, "다시 로그인");
        return button;
    }
}
