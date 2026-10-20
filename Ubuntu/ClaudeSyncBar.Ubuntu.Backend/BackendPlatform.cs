using System.Diagnostics;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Ubuntu.Backend;

/// <summary>
/// What the shared management service needs from the desktop it runs on.
/// This is Ubuntu's; the Windows app compiles its own file of the same name.
/// </summary>
internal static class BackendPlatform
{
    /// <summary>How this computer is named in the device list.</summary>
    public const string LocalDeviceName = "이 Ubuntu PC";

    /// <summary>The platform word put in place of "Windows" in the shared core's messages.</summary>
    public const string Name = "Ubuntu";

    private static string AutostartPath(WindowsPaths paths) => Path.Combine((!paths.IsIsolated ? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") : null)
        ?? Path.Combine(paths.Home, ".config"), "autostart", "claude-syncbar.desktop");

    public static bool LaunchAtLogin(WindowsPaths paths) => File.Exists(AutostartPath(paths));

    public static void SetLaunchAtLogin(WindowsPaths paths, bool enabled)
    {
        var path = AutostartPath(paths);
        WindowsPathSafety.EnsureFile(path, "자동 시작 항목");
        if (enabled)
        {
            WindowsPathSafety.EnsureDirectory(Path.GetDirectoryName(path)!, "자동 시작 디렉터리");
            File.WriteAllText(path, "[Desktop Entry]\nType=Application\nName=Claude SyncBar\nExec=claude-syncbar --background\nX-GNOME-Autostart-enabled=true\n");
        }
        else if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>A desktop notification; the service runs without a window of its own.</summary>
    public static void Notify(string message)
    {
        const string notifySend = "/usr/bin/notify-send";
        if (!File.Exists(notifySend)) return;
        try
        {
            var start = new ProcessStartInfo(notifySend) { UseShellExecute = false };
            foreach (var argument in new[] { "--app-name=Claude SyncBar", "--icon=claude-syncbar", "Claude SyncBar", message })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            process?.WaitForExit(3000);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The switch itself is recorded in the app either way.
        }
    }

    /// <summary>
    /// Whether the session is still open somewhere: Claude Desktop names the
    /// session it runs, a terminal session is known by its working directory.
    /// </summary>
    public static bool SessionStillOpen(StoppedSession session)
    {
        foreach (var process in LinuxProcess.Enumerate())
            using (process)
            {
                if (!process.IsClaudeCli) continue;
                if (process.Arguments.Any(argument => argument.Contains(session.SessionId, StringComparison.Ordinal))) return true;
                try
                {
                    if (new DirectoryInfo($"/proc/{process.Id}/cwd").LinkTarget is { } cwd && cwd == session.Cwd) return true;
                }
                // A process that cannot be inspected may be this session; it is not touched.
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
            }

        return false;
    }

    /// <summary>Runs the resume command in the background, outside this service, as the user.</summary>
    public static bool LaunchResume(StoppedSession session, IReadOnlyList<string> command, WindowsPaths paths)
    {
        const string systemdRun = "/usr/bin/systemd-run";
        var start = new ProcessStartInfo(File.Exists(systemdRun) ? systemdRun : command[0]) { UseShellExecute = false, WorkingDirectory = session.Cwd };
        if (File.Exists(systemdRun))
        {
            // A unit of its own: it outlives this service and its output is in the journal.
            foreach (var argument in new[] { "--user", "--collect", "--quiet", "--unit=claude-syncbar-resume-" + session.SessionId[..8] + "-" + DateTime.UtcNow.Ticks,
                "--working-directory=" + session.Cwd, "--setenv=PATH=" + Environment.GetEnvironmentVariable("PATH") + ":" + Path.Combine(paths.Home, ".local", "bin") })
                start.ArgumentList.Add(argument);
            if (!paths.IsIsolated && Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } config)
                start.ArgumentList.Add("--setenv=CLAUDE_CONFIG_DIR=" + config);
            start.ArgumentList.Add("--");
            foreach (var argument in command) start.ArgumentList.Add(argument);
        }
        else
        {
            foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null) return false;
            if (!File.Exists(systemdRun)) return true;
            process.WaitForExit(10000);
            return process.ExitCode == 0;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
