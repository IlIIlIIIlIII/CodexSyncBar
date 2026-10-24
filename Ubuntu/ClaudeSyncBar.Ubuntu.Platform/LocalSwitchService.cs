using System.Diagnostics;

namespace ClaudeSyncBar.Windows.Core;

public sealed class LocalSwitchService
{
    private readonly AuthStore _authStore;
    private readonly WindowsPaths _paths;
    private readonly Func<IClaudeDesktopSession> _captureDesktop;
    private readonly ClaudeDesktopAccounts _desktopAccounts;
    private readonly Func<bool> _hostedByDesktop;
    private const string HostedByDesktopMessage =
        "이 SyncBar는 Claude 데스크톱 앱 안의 터미널에서 실행되어, 데스크톱 앱을 다시 시작하면 함께 종료됩니다. "
        + "앱 목록이나 다른 터미널에서 Claude SyncBar를 실행한 뒤 다시 적용해 주세요.";
    private volatile bool _reconnectionRequired;
    // What the last switch changed in the desktop profile and the login it
    // replaced, so a rollback of that switch puts back exactly that.
    private (DesktopLoginSwap Swap, string? ReplacedAccount)? _lastDesktopSwap;

    public string? ReconnectionDetail
    {
        get
        {
            var details = new List<string>();
            if (_reconnectionRequired)
                details.Add("계정을 바꾸는 동안 실행 중이던 Claude Code 세션이 있습니다. 해당 세션을 다시 시작해 주세요.");
            if (DesktopSignInDetail() is { } desktop) details.Add(desktop);
            return details.Count == 0 ? null : string.Join(" ", details);
        }
    }

    // The first time an account is used in Claude Desktop the user signs in
    // there once; nothing here can do that for them.
    private string? DesktopSignInDetail() => _desktopAccounts.SignInState() switch
    {
        DesktopSignIn.Needed => "Claude 데스크톱 앱에서 이 계정으로 로그인해 주세요. 한 번 로그인하면 다음 전환부터 자동으로 바뀝니다.",
        DesktopSignIn.OtherAccount => "Claude 데스크톱 앱이 다른 계정으로 로그인되어 있습니다. 데스크톱 앱에서 이 계정으로 다시 로그인해 주세요.",
        _ => null,
    };

    /// <summary>
    /// Whether Claude Desktop is, or may be, signed in to another account than Claude Code's login here: the
    /// account the app last saw against the login's own. A fresh kept sign-in counts as other until the app takes it up.
    /// </summary>
    public bool DesktopUsesOtherAccount
    {
        get
        {
            if (!Directory.Exists(_desktopAccounts.ActiveDirectory)) return false;
            if (ClaudeDesktopAccounts.SignedInOwner(_desktopAccounts.ActiveDirectory) is not { } owner) return false;
            string? login;
            try { login = _authStore.ReadActiveAuth()?.Account.AccountUuid; }
            catch (ClaudeSyncBarException) { return true; }
            return !string.Equals(owner, login, StringComparison.Ordinal);
        }
    }

    /// <summary>Why the account cannot be applied to Claude Desktop from this process, or null.</summary>
    public string? DescribeDesktopBlocker(int profileId) =>
        DesktopAccountToMove(profileId) is not null && _hostedByDesktop() ? HostedByDesktopMessage : null;

    private string? DesktopAccountToMove(int profileId)
    {
        string? account;
        try { account = _authStore.ReadAuthFile(_authStore.ProfileAuthFile(profileId)).Account.AccountUuid; }
        catch (ClaudeSyncBarException) { return null; }
        return account is not null && _desktopAccounts.RequiresSwap(account) ? account : null;
    }

    private static DesktopAccount? DesktopIdentity(ClaudeOauthAccount? account) =>
        account?.AccountUuid is { Length: > 0 } id ? new(id, account.OrganizationUuid) : null;

    // The organization a registered account's desktop session records are filed under.
    private string? OrganizationOf(string account)
    {
        foreach (var id in _authStore.ExistingProfileIds())
        {
            try
            {
                var known = _authStore.ReadAuthFile(_authStore.ProfileAuthFile(id)).Account;
                if (known.AccountUuid == account) return known.OrganizationUuid;
            }
            catch (ClaudeSyncBarException) { }
        }

        return null;
    }

    // The registered accounts, by the id Claude Desktop files them under; their session records are shared.
    private IEnumerable<string> KnownAccounts()
    {
        var accounts = new List<string>();
        foreach (var id in _authStore.ExistingProfileIds())
        {
            try
            {
                if (_authStore.ReadAuthFile(_authStore.ProfileAuthFile(id)).Account.AccountUuid is { Length: > 0 } account) accounts.Add(account);
            }
            catch (ClaudeSyncBarException) { }
        }

        return accounts;
    }

    /// <summary>What applying the account does to Claude Desktop, or null when the app is not affected.</summary>
    public string? DescribeDesktopChange(int profileId)
    {
        if (DesktopAccountToMove(profileId) is not { } account) return null;
        const string change = "Claude 데스크톱 앱의 로그인만 이 계정으로 바꿉니다. 세션 목록, 앱 설정, 권한 설정, 세션 프리셋은 계정마다 따로 두지 않고 그대로 이어집니다. "
            + "앱이 실행 중이면 종료한 뒤 다시 시작하므로 진행 중인 데스크톱 세션은 잠시 멈췄다가 목록에서 이어서 열 수 있습니다.";
        if (!_desktopAccounts.HasLogin(account)) return change + " 다시 열리면 이 계정으로 한 번 로그인해 주세요.";
        // A sign-in kept by an earlier build has no tokens of the app with it.
        return _desktopAccounts.HasTokens(account) ? change
            : change + " 데스크톱 앱이 보안을 위해 다시 로그인하라고 하면 한 번만 로그인해 주세요. 그다음 전환부터는 묻지 않습니다.";
    }

    public void RefreshClientStatus()
    {
        if (_reconnectionRequired && !HasClaudeClientsRunning()) _reconnectionRequired = false;
    }

    public LocalSwitchService(AuthStore authStore, WindowsPaths paths)
        : this(authStore, paths, () => ClaudeDesktopLifecycle.Capture(paths)) { }

    internal LocalSwitchService(AuthStore authStore, WindowsPaths paths, Func<IClaudeDesktopSession> captureDesktop,
        Func<bool>? hostedByDesktop = null)
    {
        _authStore = authStore;
        _paths = paths;
        _captureDesktop = captureDesktop;
        _desktopAccounts = new(paths, OrganizationOf, KnownAccounts);
        _hostedByDesktop = hostedByDesktop ?? (() => ClaudeDesktopLifecycle.HostsThisProcess(paths.DesktopUserDataDirectory));
    }

    public int? GetActiveProfileId(IEnumerable<AccountProfile> accounts)
    {
        var accountId = _authStore.ReadActiveAccountId();
        if (accountId is null)
        {
            return null;
        }

        foreach (var account in accounts)
        {
            if (!_authStore.ProfileArtifactExists(account.Id))
            {
                // A first-run reservation has no profile auth file yet. It is
                // not an active account until the user completes login/import.
                continue;
            }

            try
            {
                var credentials = _authStore.ReadCredentials(account.Id);
                if (string.Equals(credentials.AccountId, accountId, StringComparison.Ordinal))
                {
                    return account.Id;
                }
            }
            catch (AuthenticationRequiredException)
            {
                // Keep scanning other profiles. A malformed or expired auth
                // file should not hide a valid active account.
            }
        }

        return null;
    }

    public Task SwitchAsync(int profileId)
    {
        return SwitchAsync(profileId, CancellationToken.None);
    }

    // A session that reads the credential file refreshes the active tokens on
    // its own. An unreadable environment counts: background refresh must wait.
    public bool HasClaudeClientsRunning()
    {
        foreach (var process in LinuxProcess.Enumerate())
            using (process)
                if (process.IsClaudeCli && process.ReadsCredentialFile != false
                    && (process.ClaudeHome is null || process.UsesClaudeHome(_paths))) return true;
        return false;
    }

    public async Task SwitchAsync(
        int profileId,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        _ = _authStore.ReadCredentials(profileId);
        var account = DesktopIdentity(_authStore.ReadAuthFile(_authStore.ProfileAuthFile(profileId)).Account);
        await ChangeActiveAuthAsync(() => _authStore.SwitchActive(profileId), account, restoring: false, cancellationToken);
    }

    public async Task RestoreAsync(ClaudeAuthFile? previous, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await ChangeActiveAuthAsync(() => _authStore.RestoreActive(previous), DesktopIdentity(previous?.Account), restoring: true, cancellationToken);
        if (_authStore.ReadActiveAccountId() != previous?.AccountId)
            throw new ClaudeSyncBarException("Ubuntu 계정 복구를 확인하지 못했습니다.");
    }

    private async Task ChangeActiveAuthAsync(Action changeAuth, DesktopAccount? desktopAccount, bool restoring, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Anything wrong with where the login is written must come out before the app is stopped for it.
        _paths.EnsureDirectories();
        var previous = _authStore.ReadActiveAuth();
        var clientsRunning = HasClaudeClientsRunning();
        // Rolling back the switch that made the last desktop change undoes that
        // change; anything else signs the desktop app in to the account it applies.
        var undo = restoring && _lastDesktopSwap is { } last && last.ReplacedAccount == desktopAccount?.Account ? last.Swap : null;
        var moveProfile = undo is not null || desktopAccount is not null && _desktopAccounts.RequiresSwap(desktopAccount.Account);
        // Nothing has changed yet; a restart this process would not survive is not begun.
        if (moveProfile && _hostedByDesktop()) throw new ClaudeSyncBarException(HostedByDesktopMessage);
        // Claude Desktop is restarted only when files it has open have to change.
        using var desktop = moveProfile ? _captureDesktop() : null;
        DesktopLoginSwap? swap = null;
        var undone = false;
        try
        {
            if (desktop is not null) await desktop.StopAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            changeAuth();
            if (undo is not null)
            {
                _desktopAccounts.Undo(undo);
                undone = true;
            }
            else if (desktopAccount is not null)
            {
                swap = _desktopAccounts.Activate(desktopAccount);
            }

            if (desktop is not null) await desktop.StartAsync(cancellationToken);
            _lastDesktopSwap = swap is null ? null : (swap, previous?.Account.AccountUuid);
            if (clientsRunning) _reconnectionRequired = true;
        }
        catch (Exception error)
        {
            try
            {
                // Recovery must finish even when the original request was cancelled.
                if (desktop is not null) await desktop.StopAsync(CancellationToken.None);
                _authStore.RestoreActive(previous);
                if (swap is not null) _desktopAccounts.Undo(swap);
                else if (undone) _desktopAccounts.Activate(new(undo!.TargetAccount, OrganizationOf(undo.TargetAccount)));
                if (desktop is not null) await desktop.StartAsync(CancellationToken.None);
            }
            catch (Exception recoveryError)
            {
                throw new ClaudeSyncBarException("계정 전환 실패 후 기존 계정을 복구하지 못했습니다.",
                    new AggregateException(error, recoveryError));
            }
            throw;
        }
    }

    // Running sessions keep the binary they started with; only new sessions use the update.
    public Task<string> ReconnectAfterCliUpdateAsync(string expectedVersion, CancellationToken cancellationToken = default) =>
        Task.FromResult(HasClaudeClientsRunning() ? "reconnect-pending" : "not-running");

    public void OpenClaude()
    {
        var launcher = ClaudeDesktopLifecycle.FindLauncher()
            ?? throw new ClaudeSyncBarException("Claude 데스크톱 앱을 찾지 못했습니다.");
        ClaudeDesktopLifecycle.Launch(launcher, _paths);
    }

    public void OpenClaudeHome()
    {
        _paths.EnsureDirectories();
        Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/xdg-open",
            ArgumentList = { _paths.ClaudeHome },
            UseShellExecute = true,
        });
    }
}
