// Windows port of Ubuntu/ClaudeSyncBar.Ubuntu.Platform/BrowserLoginService.cs. Keep the two in step;
// they differ only in which browser is found and how its processes and folders are reached.
using System.Diagnostics;

namespace ClaudeSyncBar.Windows.Core;

public sealed class BrowserLoginService
{
    private const string LoginUrl = "https://claude.ai/login";

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
        WindowsPathSafety.EnsurePrivateDirectory(_paths.ChromeProfilesDirectory, "Chrome 프로필 디렉터리");
        WindowsPathSafety.EnsurePrivateDirectory(
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
            || !(url.Host.Equals("claude.ai", StringComparison.OrdinalIgnoreCase)
                || url.Host.Equals("claude.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ClaudeSyncBarException("Claude Code가 반환한 로그인 주소가 허용된 Claude 주소가 아닙니다.");
        }

        try
        {
            var chrome = FindChrome();
            var start = new ProcessStartInfo(chrome) { UseShellExecute = false };
            start.ArgumentList.Add("--user-data-dir=" + ProfileDirectory(profileId));
            start.ArgumentList.Add("--no-first-run");
            start.ArgumentList.Add("--no-default-browser-check");
            start.ArgumentList.Add("--new-window");
            start.ArgumentList.Add(url.AbsoluteUri);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Chrome 프로세스를 시작하지 못했습니다.");
            lock (_processGate)
            {
                if (!_launchedProcessIds.TryGetValue(profileId, out var ids))
                    _launchedProcessIds[profileId] = ids = [];
                ids.Add(process.Id);
            }
        }
        catch (Exception error) when (
            error is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            throw new ClaudeSyncBarException("계정 전용 브라우저 로그인 창을 열지 못했습니다.", error);
        }

        lock (_processGate)
        {
            _lastLoginUrls[profileId] = url;
        }
    }

    // Chrome when it is installed; otherwise Edge, which every Windows 11 has. Both keep a profile per --user-data-dir.
    public static string FindChrome()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var candidate in new[]
        {
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
        })
            if (File.Exists(candidate)) return candidate;
        throw new ClaudeSyncBarException("계정별 로그인에 Chrome 또는 Microsoft Edge가 필요합니다.");
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
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            ArgumentList = { _paths.ClaudeHome },
            UseShellExecute = false,
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
            throw new ClaudeSyncBarException("Chrome 프로필 경로가 앱 전용 폴더 밖에 있어 삭제를 중단했습니다.");
        }

        if (File.Exists(directory)
            || Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ClaudeSyncBarException("Chrome 프로필 저장소가 안전한 디렉터리가 아닙니다.");
        }

        WindowsPathSafety.EnsurePrivateDirectory(_paths.ChromeProfilesDirectory, "Chrome 프로필 디렉터리");
        CloseLoginWindow(profileId);
        var backupRoot = Path.Combine(_paths.ChromeProfilesDirectory, "Backups");
        WindowsPathSafety.EnsurePrivateDirectory(backupRoot, "Chrome 프로필 백업 디렉터리");
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
                throw new ClaudeSyncBarException(
                    "전용 Chrome 창을 완전히 닫은 뒤 다시 시도해 주세요. 기존 로그인 데이터는 백업으로 보존했습니다.",
                    error);
            }
        }

        WindowsPathSafety.EnsurePrivateDirectory(directory, "Chrome 계정 프로필 디렉터리");
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
        var profile = _paths.ChromeProfileDirectory(profileId);
        foreach (var process in WindowsProcess.Enumerate())
            using (process)
                if (process.UsesBrowserProfile(profile) && Path.GetFileName(process.Executable).ToLowerInvariant() is "chrome.exe" or "msedge.exe")
                    process.StopAsync(CancellationToken.None, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        lock (_processGate) _launchedProcessIds.Remove(profileId);
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
