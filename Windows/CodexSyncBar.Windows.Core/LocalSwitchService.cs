using System.Diagnostics;
using System.Management;
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
        return confirmed.Count > 0 || _reconnectionRequired;
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
            throw new CodexSyncBarException("Windows 계정 복구를 확인하지 못했습니다.");
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
            changeAuth();
            await StopCodexClientsAsync(cancellationToken);
            await desktop.StartAsync(cancellationToken);
        }
        catch (Exception error)
        {
            try
            {
                // Recovery must finish even when the original request was cancelled.
                await desktop.StopAsync(CancellationToken.None);
                _authStore.RestoreActive(previous);
                await StopCodexClientsAsync(CancellationToken.None);
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
                    if (executable is null || !Path.GetFileName(executable).Equals("codex.exe", StringComparison.OrdinalIgnoreCase)) continue;
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
        if (!OperatingSystem.IsWindows()) return;
        var processes = GetConfirmedAppServerProcesses();
        foreach (var observed in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StillConfirmed(observed)) continue;
            try
            {
                using var process = Process.GetProcessById(observed.Id);
                var created = ManagementDateTimeConverter.ToDateTime(observed.CreationDate).ToUniversalTime();
                if ((process.StartTime.ToUniversalTime() - created).Duration() > TimeSpan.FromMilliseconds(1))
                {
                    _reconnectionRequired = true;
                    continue; // PID has been reused, or WMI could not identify this process exactly.
                }
                if (process.HasExited) continue;
                process.Kill(entireProcessTree: false); // A child may be an unrelated CLI or use a different home.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (ArgumentException) { /* It exited between enumeration and opening the process. */ }
            catch (InvalidOperationException) { /* The captured process has already exited. */ }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                throw new CodexSyncBarException("확인된 Codex app-server 연결을 종료하지 못했습니다.", error);
            }
        }
    }

    public static bool IsCodexAppServerCommandLine(string processName, string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)
            || !CodexInvocationPattern.IsMatch(commandLine)
            || !AppServerPattern.IsMatch(commandLine))
        {
            return false;
        }

        return processName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("codex.cmd", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("node.exe", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("codex-app-server.exe", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ObservedProcess(int Id, int SessionId, string CreationDate);

    private IReadOnlyList<ObservedProcess> GetConfirmedAppServerProcesses()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var currentSessionId = Process.GetCurrentProcess().SessionId;
        var processes = new List<ObservedProcess>();
        var unconfirmed = false;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, ProcessId, SessionId, CommandLine, CreationDate FROM Win32_Process");
            using var rows = searcher.Get();
            foreach (ManagementObject row in rows)
            {
                using (row)
                {
                    if (!TryReadInt(row["SessionId"], out var sessionId) || sessionId != currentSessionId
                        || !TryReadInt(row["ProcessId"], out var processId)) continue;
                    var target = CodexProcessScope.Classify(row["Name"] as string ?? "", row["CommandLine"] as string ?? "", _paths.CodexHome);
                    var creation = row["CreationDate"] as string;
                    if (target == CodexProcessTarget.CurrentHome && !string.IsNullOrWhiteSpace(creation))
                        processes.Add(new(processId, sessionId, creation));
                    else if (target == CodexProcessTarget.Unknown || target == CodexProcessTarget.CurrentHome)
                        unconfirmed = true;
                }
            }
        }
        catch (Exception error) when (error is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Lack of process evidence must never turn into a broad kill or block an auth-file switch.
            _reconnectionRequired = true;
            return [];
        }
        _reconnectionRequired = unconfirmed;
        return processes;
    }

    private bool StillConfirmed(ObservedProcess observed)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT Name, SessionId, CommandLine, CreationDate FROM Win32_Process WHERE ProcessId={observed.Id}");
            using var rows = searcher.Get();
            foreach (ManagementObject row in rows)
            {
                using (row)
                {
                    var confirmed = TryReadInt(row["SessionId"], out var sessionId) && sessionId == observed.SessionId
                        && string.Equals(row["CreationDate"] as string, observed.CreationDate, StringComparison.Ordinal)
                        && CodexProcessScope.Classify(row["Name"] as string ?? "", row["CommandLine"] as string ?? "", _paths.CodexHome)
                            == CodexProcessTarget.CurrentHome;
                    if (!confirmed) _reconnectionRequired = true;
                    return confirmed;
                }
            }
            return false;
        }
        catch (Exception error) when (error is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            _reconnectionRequired = true;
            return false;
        }
    }

    private static bool TryReadInt(object? value, out int result) =>
        int.TryParse(value?.ToString(), out result);

    public void OpenCodex()
    {
        var executable = CodexCliLocator.Find();
        if (executable is null)
        {
            throw new CodexSyncBarException("Codex CLI를 찾지 못했습니다. 설치 후 다시 시도해 주세요.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            WorkingDirectory = _paths.Home,
        });
    }

    public void OpenCodexHome()
    {
        _paths.EnsureDirectories();
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_paths.CodexHome}\"",
            UseShellExecute = true,
        });
    }

}
