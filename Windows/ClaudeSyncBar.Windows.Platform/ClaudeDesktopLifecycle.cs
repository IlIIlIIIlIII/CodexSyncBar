using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClaudeSyncBar.Windows.Core;

internal interface IClaudeDesktopSession : IDisposable
{
    Task StopAsync(CancellationToken cancellationToken);
    Task StartAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Stops and restarts Claude Desktop around a change of its sign-in. On Windows
/// the app is a Store package (or, for older installs, a per-user Squirrel
/// install); it is started again the way the Start menu starts it.
/// </summary>
internal static class ClaudeDesktopLifecycle
{
    private static readonly Regex StoreExecutable = new(@"\\WindowsApps\\Claude_[^\\]+__(?<publisher>[a-z0-9]+)\\app\\claude\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SquirrelExecutable = new(@"\\AnthropicClaude\\(?:app-[^\\]+\\)?claude\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsDesktopExecutable(string path) => StoreExecutable.IsMatch(path) || SquirrelExecutable.IsMatch(path);

    private static bool IsRoot(WindowsProcess process) => IsDesktopExecutable(process.Executable)
        && !process.Arguments.Skip(1).Any(argument => argument.StartsWith("--type=", StringComparison.Ordinal));

    /// <summary>How to start the app: a Store app id, or the Squirrel launcher. Null when it is not installed.</summary>
    internal static string? FindLauncher(WindowsPaths? paths = null)
    {
        paths ??= new WindowsPaths();
        var packages = Path.Combine(paths.LocalAppData, "Packages");
        if (Directory.Exists(packages))
            foreach (var family in Directory.EnumerateDirectories(packages, "Claude_*").Select(Path.GetFileName))
                if (family is not null && Regex.IsMatch(family, "^Claude_[a-z0-9]{13}$")) return "shell:AppsFolder\\" + family + "!Claude";
        var squirrel = Path.Combine(paths.LocalAppData, "AnthropicClaude", "claude.exe");
        return File.Exists(squirrel) ? squirrel : null;
    }

    // The Store package runs from a versioned folder; it is started by its app id, never by that path.
    private static string LauncherFor(string executable) => StoreExecutable.Match(executable) is { Success: true } store
        ? "shell:AppsFolder\\Claude_" + store.Groups["publisher"].Value + "!Claude"
        : executable;

    internal static void Launch(string launcher, WindowsPaths paths)
    {
        var start = launcher.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false }
            : new ProcessStartInfo(launcher) { UseShellExecute = false, WorkingDirectory = paths.Home };
        if (launcher.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(launcher);
        // Nothing of this app's own environment, such as a Claude configuration directory, is handed on.
        foreach (var name in start.Environment.Keys.Where(key => key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        using var process = Process.Start(start);
    }

    private static WindowsProcess? Root()
    {
        WindowsProcess? root = null;
        foreach (var process in WindowsProcess.Enumerate())
        {
            if (root is null && IsRoot(process)) root = process;
            else process.Dispose();
        }

        return root;
    }

    /// <summary>
    /// True when this process was started from inside the running app, for
    /// example from its Code tab. Stopping the app would end this process
    /// before it could start the app again.
    /// </summary>
    internal static bool HostsThisProcess(string userDataDirectory)
    {
        using var root = Root();
        return root is not null && WindowsProcess.DescendsFrom(Environment.ProcessId, root.Id);
    }

    internal static IClaudeDesktopSession Capture(WindowsPaths paths, Action<string, WindowsPaths>? launch = null)
    {
        // An isolated home must never close the real user's app.
        if (paths.IsIsolated) return new DesktopSession(paths, null, launch ?? Launch);
        using var root = Root();
        return new DesktopSession(paths, root is null ? null : LauncherFor(root.Executable), launch ?? Launch);
    }

    private sealed class DesktopSession(WindowsPaths paths, string? launcher, Action<string, WindowsPaths> launch) : IClaudeDesktopSession
    {
        // Remembered from the first stop, so a recovery that stops the app again still restarts it.
        private string? _launcher = launcher;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (paths.IsIsolated) return;
            using (var root = Root())
            {
                if (root is not null)
                {
                    _launcher ??= LauncherFor(root.Executable);
                    await root.StopAsync(cancellationToken, TimeSpan.FromSeconds(4));
                }
            }

            // Helpers can outlive the main process for a moment and may still be writing.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (true)
            {
                var busy = false;
                foreach (var process in WindowsProcess.Enumerate())
                    using (process)
                        busy |= IsDesktopExecutable(process.Executable);
                if (!busy && ProfileReleased()) return;
                if (DateTime.UtcNow >= deadline)
                    throw new ClaudeSyncBarException("Claude 데스크톱 앱이 완전히 종료되지 않았습니다. 앱을 직접 종료한 뒤 다시 시도해 주세요.");
                await Task.Delay(200, cancellationToken);
            }
        }

        // Chromium holds "lockfile" in the profile for as long as it uses it.
        private bool ProfileReleased()
        {
            var lockFile = Path.Combine(paths.DesktopUserDataDirectory, "lockfile");
            if (!File.Exists(lockFile)) return true;
            try
            {
                using var stream = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_launcher is null || paths.IsIsolated) return;
            using (var running = Root())
                if (running is not null) return;
            launch(_launcher, paths);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var started = Root())
                    if (started is not null) return;
                await Task.Delay(250, cancellationToken);
            }

            throw new ClaudeSyncBarException("Claude 데스크톱 앱이 다시 시작되지 않았습니다. 이전 상태를 복구합니다.");
        }

        public void Dispose() { }
    }
}
