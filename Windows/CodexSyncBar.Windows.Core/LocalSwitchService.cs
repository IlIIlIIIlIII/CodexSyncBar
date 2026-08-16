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
        """(?:^|\s)app-server\s+(?:proxy(?:\s|$)|--listen\s+["']?unix://)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly AuthStore _authStore;
    private readonly WindowsPaths _paths;

    public LocalSwitchService(AuthStore authStore, WindowsPaths paths)
    {
        _authStore = authStore;
        _paths = paths;
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
        try
        {
            return GetCodexAppServerProcesses().Count > 0;
        }
        catch
        {
            // A process-query failure must defer credential mutation rather
            // than risk racing a client whose command line could not be read.
            return true;
        }
    }

    public async Task SwitchAsync(
        int profileId,
        CancellationToken cancellationToken)
    {
        var previous = _authStore.ReadActiveAuth();
        _authStore.SwitchActive(profileId);
        try
        {
            await StopCodexClientsAsync(cancellationToken);
        }
        catch
        {
            _authStore.RestoreActive(previous);
            throw;
        }
    }

    private async Task StopCodexClientsAsync(CancellationToken cancellationToken)
    {
        var processes = GetCodexAppServerProcesses();
        foreach (var process in processes)
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception error) when (
                    error is InvalidOperationException
                    or ArgumentException
                    or System.ComponentModel.Win32Exception)
                {
                    throw new CodexSyncBarException(
                        "현재 실행 중인 Codex app-server를 종료하지 못해 계정 전환을 중단했습니다.",
                        error);
                }
            }
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stillRunning = GetCodexAppServerProcesses().Count > 0;
            if (!stillRunning)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw new CodexSyncBarException("Codex app-server가 종료될 때까지 기다리지 못해 계정 전환을 중단했습니다.");
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

    private static IReadOnlyList<Process> GetCodexAppServerProcesses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var currentSessionId = Process.GetCurrentProcess().SessionId;
        var processes = new List<Process>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, ProcessId, SessionId, CommandLine FROM Win32_Process");
            using var rows = searcher.Get();
            foreach (ManagementObject row in rows)
            {
                var processName = row["Name"] as string ?? string.Empty;
                var commandLine = row["CommandLine"] as string ?? string.Empty;
                if (!IsCodexAppServerCommandLine(processName, commandLine)
                    || !TryReadInt(row["SessionId"], out var sessionId)
                    || sessionId != currentSessionId
                    || !TryReadInt(row["ProcessId"], out var processId))
                {
                    row.Dispose();
                    continue;
                }

                try
                {
                    processes.Add(Process.GetProcessById(processId));
                }
                catch (ArgumentException)
                {
                    // The process exited between WMI enumeration and opening.
                }
                finally
                {
                    row.Dispose();
                }
            }
        }
        catch (Exception error) when (
            error is ManagementException
            or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException)
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }

            throw new CodexSyncBarException(
                "실행 중인 Codex app-server를 확인하지 못해 계정 전환을 보류했습니다.",
                error);
        }

        return processes
            .GroupBy(process => process.Id)
            .Select(group => group.First())
            .ToArray();
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
