using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

public sealed class LocalSwitchService
{
    private static readonly Regex CodexInvocationPattern = new(
        """(?:^|[\\/\s"'])codex(?:\.exe|\.cmd|\.js)?(?:["'\s]|$)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AppServerPattern = new(
        """(?:^|\s)app-server\s+(?:proxy(?:\s|$)|--listen(?:\s+|=)["']?unix://)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly AuthStore _authStore;
    private readonly WindowsPaths _paths;
    private readonly Func<ICodexDesktopSession> _captureDesktop;
    private volatile bool _reconnectionRequired;

    public string? ReconnectionDetail => _reconnectionRequired
        ? "인증 경로를 확인하지 못한 Codex 연결은 유지했습니다. 해당 Codex 창을 다시 연결해 주세요."
        : null;

    public void RefreshClientStatus() => _ = GetConfirmedAppServerProcesses();

    public LocalSwitchService(AuthStore authStore, WindowsPaths paths)
        : this(authStore, paths, () => CodexDesktopLifecycle.Capture(paths)) { }

    internal LocalSwitchService(AuthStore authStore, WindowsPaths paths, Func<ICodexDesktopSession> captureDesktop)
    {
        _authStore = authStore;
        _paths = paths;
        _captureDesktop = captureDesktop;
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

    public bool HasCodexClientsRunning()
    {
        var confirmed = GetConfirmedAppServerProcesses();
        // Unknown clients are retained, and active-token background refresh is deferred.
        if (confirmed.Count > 0 || _reconnectionRequired) return true;
        foreach (var process in LinuxProcess.Enumerate())
            using (process)
                if (CodexDesktopLifecycle.IsDesktopExecutable(process.Executable)
                    && (process.CodexHome is null || process.UsesCodexHome(_paths))) return true;
        return false;
    }

    public async Task SwitchAsync(
        int profileId,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        _ = _authStore.ReadCredentials(profileId);
        await ChangeActiveAuthAsync(() => _authStore.SwitchActive(profileId), cancellationToken);
    }

    public async Task RestoreAsync(CodexAuthFile? previous, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await ChangeActiveAuthAsync(() => _authStore.RestoreActive(previous), cancellationToken);
        if (_authStore.ReadActiveAccountId() != previous?.Tokens.AccountId)
            throw new CodexSyncBarException("Ubuntu 계정 복구를 확인하지 못했습니다.");
    }

    private async Task ChangeActiveAuthAsync(Action changeAuth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previous = _authStore.ReadActiveAuth();
        using var desktop = _captureDesktop();
        try
        {
            await desktop.StopAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await StopCodexClientsAsync(cancellationToken);
            changeAuth();
            await desktop.StartAsync(cancellationToken);
        }
        catch (Exception error)
        {
            try
            {
                // Recovery must finish even when the original request was cancelled.
                await desktop.StopAsync(CancellationToken.None);
                await StopCodexClientsAsync(CancellationToken.None);
                _authStore.RestoreActive(previous);
                await desktop.StartAsync(CancellationToken.None);
            }
            catch (Exception recoveryError)
            {
                throw new CodexSyncBarException("계정 전환 실패 후 Codex 앱과 기존 계정을 복구하지 못했습니다.",
                    new AggregateException(error, recoveryError));
            }
            throw;
        }
    }

    public async Task<string> ReconnectAfterCliUpdateAsync(string expectedVersion, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var hadClients = GetConfirmedAppServerProcesses().Count > 0;
        await StopCodexClientsAsync(cancellationToken);
        if (!hadClients) return _reconnectionRequired ? "reconnect-pending" : "not-running";
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            foreach (var observed in GetConfirmedAppServerProcesses())
            {
                if (!StillConfirmed(observed)) continue;
                try
                {
                    using var process = Process.GetProcessById(observed.Id);
                    var executable = process.MainModule?.FileName;
                    if (executable is null || !Path.GetFileName(executable).Equals("codex", StringComparison.Ordinal)) continue;
                    var version = await ProcessRunner.RunAsync(executable, ["--version"], cancellationToken: cancellationToken, timeout: TimeSpan.FromSeconds(5));
                    if (version.ExitCode == 0 && version.StandardOutput.Trim() == "codex-cli " + expectedVersion)
                        return "reconnected";
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return "reconnect-pending";
    }

    private async Task StopCodexClientsAsync(CancellationToken cancellationToken)
    {
        foreach (var process in LinuxProcess.Enumerate())
            using (process)
                if (process.IsAppServer && process.UsesCodexHome(_paths)) await process.StopAsync(cancellationToken);
    }

    public static bool IsCodexAppServerCommandLine(string processName, string commandLine) =>
        (processName is "codex" or "codex.js" or "node" or "nodejs") && CodexInvocationPattern.IsMatch(commandLine) && AppServerPattern.IsMatch(commandLine);

    private sealed record ObservedProcess(int Id, string Executable);
    private IReadOnlyList<ObservedProcess> GetConfirmedAppServerProcesses()
    {
        var confirmed = new List<ObservedProcess>();
        var unknown = false;
        foreach (var process in LinuxProcess.Enumerate())
            using (process)
            {
                if (!process.IsAppServer) continue;
                if (process.UsesCodexHome(_paths)) confirmed.Add(new(process.Id, process.Executable));
                else if (process.CodexHome is null) unknown = true;
            }
        _reconnectionRequired = unknown;
        return confirmed;
    }
    private bool StillConfirmed(ObservedProcess observed)
    {
        foreach (var process in LinuxProcess.Enumerate())
            using (process) if (process.Id == observed.Id) return process.IsAppServer && process.UsesCodexHome(_paths) && process.Executable == observed.Executable;
        return false;
    }

    public void OpenCodex()
    {
        var launcher = new[] { "/usr/bin/chatgpt", "/usr/lib/chatgpt/codex-launcher", "/opt/codex/codex", "/opt/Codex/codex" }.FirstOrDefault(File.Exists)
            ?? throw new CodexSyncBarException("Codex 데스크톱 앱을 찾지 못했습니다.");
        var start = new ProcessStartInfo(launcher) { UseShellExecute = false, WorkingDirectory = _paths.Home };
        start.Environment["CODEX_HOME"] = _paths.CodexHome;
        Process.Start(start)?.Dispose();
    }

    public void OpenCodexHome()
    {
        _paths.EnsureDirectories();
        Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/xdg-open",
            ArgumentList = { _paths.CodexHome },
            UseShellExecute = true,
        });
    }

}
