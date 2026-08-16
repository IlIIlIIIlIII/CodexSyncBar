using System.Diagnostics;
using System.Management;

namespace CodexSyncBar.Windows.Core;

public sealed class BrowserLoginService
{
    private const string LoginUrl = "https://auth.openai.com/";

    private readonly WindowsPaths _paths;
    private readonly BrowserCleanupStateStore _cleanupState;
    private readonly object _processGate = new();
    private readonly Dictionary<int, HashSet<int>> _launchedProcessIds = [];
    private readonly Dictionary<int, Uri> _lastLoginUrls = [];

    public BrowserLoginService(WindowsPaths paths)
    {
        _paths = paths;
        _cleanupState = new BrowserCleanupStateStore(paths);
    }

    public string ProfileDirectory(int profileId)
    {
        _paths.EnsureDirectories();
        WindowsPathSafety.EnsureDirectory(_paths.ChromeProfilesDirectory, "Chrome 프로필 디렉터리");
        WindowsPathSafety.EnsureDirectory(
            Path.Combine(_paths.ChromeProfilesDirectory, $"profile-{profileId}"),
            "Chrome 계정 프로필 디렉터리");
        var directory = _paths.ChromeProfileDirectory(profileId);
        return directory;
    }

    public void OpenLogin(int profileId)
    {
        OpenUrl(profileId, new Uri(LoginUrl));
    }

    public void OpenUrl(int profileId, Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps
            || !url.Host.Equals("auth.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new CodexSyncBarException("Codex가 반환한 로그인 주소가 허용된 OpenAI 주소가 아닙니다.");
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception error) when (
            error is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            throw new CodexSyncBarException("Windows 기본 브라우저로 로그인 페이지를 열지 못했습니다.", error);
        }

        lock (_processGate)
        {
            _lastLoginUrls[profileId] = url;
        }
    }

    public void ReopenLogin(int profileId)
    {
        Uri url;
        lock (_processGate)
        {
            url = _lastLoginUrls.GetValueOrDefault(profileId) ?? new Uri(LoginUrl);
        }

        OpenUrl(profileId, url);
    }

    public string ResetProfileForLogin(int profileId) => ClearProfile(profileId);

    public void OpenAuthFileFolder()
    {
        _paths.EnsureDirectories();
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{_paths.ActiveAuthFile}\"",
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Removes a profile without destroying the user's data first. The whole
    /// directory is moved to a private backup so account removal can be
    /// retried safely if Chrome still owns a file handle.
    /// </summary>
    public string ClearProfile(int profileId)
    {
        var directory = Path.GetFullPath(_paths.ChromeProfileDirectory(profileId));
        var root = Path.GetFullPath(_paths.ChromeProfilesDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new CodexSyncBarException("Chrome 프로필 경로가 앱 전용 폴더 밖에 있어 삭제를 중단했습니다.");
        }

        if (File.Exists(directory)
            || Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new CodexSyncBarException("Chrome 프로필 저장소가 안전한 디렉터리가 아닙니다.");
        }

        WindowsPathSafety.EnsureDirectory(_paths.ChromeProfilesDirectory, "Chrome 프로필 디렉터리");
        CloseLoginWindow(profileId);
        var backupRoot = Path.Combine(_paths.ChromeProfilesDirectory, "Backups");
        WindowsPathSafety.EnsureDirectory(backupRoot, "Chrome 프로필 백업 디렉터리");
        var backup = Path.Combine(
            backupRoot,
            $"profile-{profileId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        if (Directory.Exists(directory))
        {
            try
            {
                Directory.Move(directory, backup);
            }
            catch (Exception error)
            {
                throw new CodexSyncBarException(
                    "전용 Chrome 창을 완전히 닫은 뒤 다시 시도해 주세요. 기존 로그인 데이터는 백업으로 보존했습니다.",
                    error);
            }
        }

        WindowsPathSafety.EnsureDirectory(directory, "Chrome 계정 프로필 디렉터리");
        var pending = _cleanupState.Load().ToHashSet();
        pending.Remove(profileId);
        _cleanupState.Save(pending);
        return backup;
    }

    /// <summary>
    /// Closes only the Chrome processes launched for this account's isolated
    /// login profile. Failure is intentionally best effort: Chrome may have
    /// handed the window to an existing browser process, in which case the
    /// later profile cleanup will keep its retry marker.
    /// </summary>
    public void CloseLoginWindow(int profileId)
    {
        // Closing a login window must not recreate a profile that has already
        // been removed. The login path creates the directory before launch;
        // cleanup/recovery paths may intentionally call this after it is gone.
        var profile = Path.GetFullPath(_paths.ChromeProfileDirectory(profileId));
        int[] processIds;
        lock (_processGate)
        {
            var tracked = _launchedProcessIds.TryGetValue(profileId, out var ids)
                ? ids
                : [];
            tracked.UnionWith(FindChromeProcessIds(profile));
            processIds = tracked.ToArray();
            _launchedProcessIds.Remove(profileId);
        }

        foreach (var processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var stillRunning = false;
            foreach (var processId in processIds)
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    stillRunning |= !process.HasExited;
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }

            if (!stillRunning)
            {
                return;
            }

            Thread.Sleep(50);
        }
    }

    private static HashSet<int> FindChromeProcessIds(string profileDirectory)
    {
        var processIds = new HashSet<int>();
        if (!OperatingSystem.IsWindows())
        {
            return processIds;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'chrome.exe'");
            using var results = searcher.Get();
            foreach (ManagementObject process in results)
            {
                var commandLine = process["CommandLine"] as string;
                if (!int.TryParse(process["ProcessId"]?.ToString(), out var processId)
                    || processId <= 0
                    || string.IsNullOrWhiteSpace(commandLine)
                    || !CommandLineUsesProfile(commandLine, profileDirectory))
                {
                    continue;
                }

                processIds.Add(processId);
            }
        }
        catch (ManagementException)
        {
            // The tracked root process is still closed below. WMI is only
            // needed to find a Chrome process that adopted the login window.
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }

        return processIds;
    }

    private static bool CommandLineUsesProfile(string commandLine, string profileDirectory)
    {
        var expected = NormalizeProfilePath(profileDirectory);
        const string option = "--user-data-dir";
        var start = 0;
        while ((start = commandLine.IndexOf(option, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (start > 0 && !char.IsWhiteSpace(commandLine[start - 1]))
            {
                start += option.Length;
                continue;
            }

            var cursor = start + option.Length;
            while (cursor < commandLine.Length && char.IsWhiteSpace(commandLine[cursor]))
            {
                cursor++;
            }

            if (cursor < commandLine.Length && commandLine[cursor] == '=')
            {
                cursor++;
                while (cursor < commandLine.Length && char.IsWhiteSpace(commandLine[cursor]))
                {
                    cursor++;
                }
            }

            if (cursor >= commandLine.Length)
            {
                break;
            }

            var quoted = commandLine[cursor] == '"';
            if (quoted)
            {
                cursor++;
            }

            var end = quoted
                ? commandLine.IndexOf('"', cursor)
                : FindWhitespace(commandLine, cursor);
            if (end < 0)
            {
                end = commandLine.Length;
            }

            var value = commandLine[cursor..end];
            if (string.Equals(NormalizeProfilePath(value), expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            start = Math.Max(end, start + option.Length);
        }

        return false;
    }

    private static int FindWhitespace(string value, int start)
    {
        for (var index = start; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static string NormalizeProfilePath(string value)
    {
        var normalized = value.Trim().Trim('"');
        try
        {
            return Path.GetFullPath(normalized)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            return normalized;
        }
    }

    public void MarkCleanupPending(int profileId)
    {
        var pending = _cleanupState.Load().ToHashSet();
        pending.Add(profileId);
        _cleanupState.Save(pending);
    }

    public IReadOnlyList<int> RecoverPendingProfiles()
    {
        var pending = _cleanupState.Load();
        var remaining = new HashSet<int>(pending);
        foreach (var profileId in pending)
        {
            try
            {
                ClearProfile(profileId);
                remaining.Remove(profileId);
            }
            catch
            {
                // Keep the marker for the next launch; Chrome may still own a
                // profile database file.
            }
        }

        _cleanupState.Save(remaining);
        return remaining.Order().ToArray();
    }

}
