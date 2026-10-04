using System.Diagnostics;

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
            || !url.Host.Equals("auth.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new CodexSyncBarException("Codex가 반환한 로그인 주소가 허용된 OpenAI 주소가 아닙니다.");
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
            throw new CodexSyncBarException("계정 전용 Chrome 로그인 창을 열지 못했습니다.", error);
        }

        lock (_processGate)
        {
            _lastLoginUrls[profileId] = url;
        }
    }

    public static string FindChrome()
    {
        foreach (var candidate in new[] { "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/chromium", "/snap/bin/chromium" })
            if (File.Exists(candidate)) return candidate;
        throw new CodexSyncBarException("계정별 로그인에 Chrome 또는 Chromium이 필요합니다.");
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
            FileName = "/usr/bin/xdg-open",
            ArgumentList = { _paths.CodexHome },
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
        if (!directory.StartsWith(root, StringComparison.Ordinal))
        {
            throw new CodexSyncBarException("Chrome 프로필 경로가 앱 전용 폴더 밖에 있어 삭제를 중단했습니다.");
        }

        if (File.Exists(directory)
            || Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new CodexSyncBarException("Chrome 프로필 저장소가 안전한 디렉터리가 아닙니다.");
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
                throw new CodexSyncBarException(
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
        foreach (var process in LinuxProcess.Enumerate())
            using (process)
                if (process.UsesBrowserProfile(profile) && Path.GetFileName(process.Executable) is "chrome" or "chromium" or "chromium-browser")
                    process.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
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
