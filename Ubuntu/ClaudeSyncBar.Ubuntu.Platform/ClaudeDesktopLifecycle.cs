using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeSyncBar.Windows.Core;

internal interface IClaudeDesktopSession : IDisposable
{
    Task StopAsync(CancellationToken cancellationToken);
    Task StartAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Stops and restarts the Claude Desktop instance that has the default
/// profile directory open, so that directory can be exchanged in between.
/// Instances started on another profile directory are never touched.
/// </summary>
internal static class ClaudeDesktopLifecycle
{
    private const string SystemdRun = "/usr/bin/systemd-run";
    private const string SystemdCat = "/usr/bin/systemd-cat";
    private const string Setsid = "/usr/bin/setsid";
    private const string JournalSocket = "/run/systemd/journal/stdout";
    // The name GNOME logs this app under when it starts it.
    private const string JournalIdentifier = "com.anthropic.Claude.desktop";
    private static readonly Regex ChildType = new(@"(?:^|\s)--type(?:=|\s|$)", RegexOptions.CultureInvariant);
    private static readonly string[] Launchers = ["/usr/bin/claude-desktop", "/usr/lib/claude-desktop/claude-desktop"];

    internal static bool IsDesktopExecutable(string path) => Path.GetFileName(path) == "claude-desktop";

    internal static string? FindLauncher() => Launchers.FirstOrDefault(File.Exists);

    /// <summary>
    /// The process that has a profile directory open. Chromium keeps a symbolic
    /// link named "host-pid" in the directory for as long as it uses it.
    /// </summary>
    internal static int? LockHolder(string userDataDirectory)
    {
        string? target;
        try { target = new FileInfo(Path.Combine(userDataDirectory, "SingletonLock")).LinkTarget; }
        catch (IOException) { return null; }
        if (target is null) return null;
        var separator = target.LastIndexOf('-');
        if (separator <= 0 || !int.TryParse(target[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 1)
            return null;
        // A home directory shared over the network can be open on another machine,
        // where this app can neither see nor stop the process.
        if (!string.Equals(target[..separator], System.Net.Dns.GetHostName(), StringComparison.Ordinal))
            throw new ClaudeSyncBarException("Claude 데스크톱 앱이 다른 컴퓨터에서 이 데이터 폴더를 사용 중입니다. 그 앱을 종료한 뒤 다시 시도해 주세요.");
        return pid;
    }

    internal static bool IsDesktopRoot(LinuxProcess process) =>
        IsDesktopExecutable(process.Executable) && !process.Arguments.Any(argument => ChildType.IsMatch(argument));

    // A lock left by a crashed app names a process that is gone, or one that
    // has since been reused by something else; neither is a running instance.
    private static LinuxProcess? Holder(string userDataDirectory)
    {
        if (LockHolder(userDataDirectory) is not { } pid) return null;
        var process = LinuxProcess.Capture(pid);
        if (process is not null && !IsDesktopRoot(process))
        {
            process.Dispose();
            return null;
        }

        return process;
    }

    /// <summary>
    /// True when this process was started from inside the running app, for
    /// example from its terminal panel. Stopping the app would end this
    /// process before it could start the app again.
    /// </summary>
    internal static bool HostsThisProcess(string userDataDirectory)
    {
        using var holder = Holder(userDataDirectory);
        return holder is not null && LinuxProcess.DescendsFrom(Environment.ProcessId, holder.Id);
    }

    internal static IClaudeDesktopSession Capture(WindowsPaths paths, Action<string, WindowsPaths>? launch = null)
    {
        using var holder = Holder(paths.DesktopUserDataDirectory);
        return new DesktopSession(paths, holder?.Executable, launch ?? Launch);
    }

    /// <summary>
    /// Starts Claude Desktop the way the desktop itself would: detached from
    /// this service, in a scope of its own, with the login session's
    /// environment and its output in the journal under the app's own name.
    /// </summary>
    internal static void Launch(string executable, WindowsPaths paths)
    {
        var session = LoginSessionEnvironment();
        var command = new List<string>();
        // Otherwise the app would write into this service's log for as long as it runs.
        if (File.Exists(SystemdCat) && File.Exists(JournalSocket)) command.AddRange([SystemdCat, "--identifier=" + JournalIdentifier]);
        // A scope of its own, so it is not stopped together with this service.
        if (session is not null)
            command.AddRange([SystemdRun, "--user", "--scope", "--collect", "--quiet", "--slice=app.slice", "--"]);
        command.Add(executable);

        // The starter leaves at once: the app is nobody's child here and nothing waits on it.
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = paths.Home };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(File.Exists(Setsid)
            ? "exec " + Setsid + " --fork \"$@\" </dev/null >/dev/null 2>&1"
            : "\"$@\" </dev/null >/dev/null 2>&1 &");
        start.ArgumentList.Add("sh");
        foreach (var argument in command) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var (key, value) in WithIdentity(session ?? InheritedSessionEnvironment(), paths)) start.Environment[key] = value;
        using var starter = Process.Start(start);
        starter?.WaitForExit(5000);
    }

    // The app gets what the desktop gives any application it starts: input
    // method, SSH agent, shell and so on. Nothing of this service's own
    // environment, such as its Claude configuration, is handed on.
    internal static Dictionary<string, string> SessionEnvironment(WindowsPaths paths) =>
        WithIdentity(LoginSessionEnvironment() ?? InheritedSessionEnvironment(), paths);

    private static Dictionary<string, string> WithIdentity(Dictionary<string, string> environment, WindowsPaths paths)
    {
        environment["HOME"] = paths.Home;
        environment["USER"] = Environment.UserName;
        environment["LOGNAME"] = Environment.UserName;
        return environment;
    }

    // The user's service manager holds the environment of the graphical login.
    // A short-lived unit prints it; nothing else reports the values unescaped.
    private static Dictionary<string, string>? LoginSessionEnvironment()
    {
        if (!File.Exists(SystemdRun) || !File.Exists("/usr/bin/env")) return null;
        try
        {
            var start = new ProcessStartInfo(SystemdRun)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            foreach (var argument in new[] { "--user", "--pipe", "--wait", "--collect", "--quiet", "--", "/usr/bin/env", "-0" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return null;
            process.StandardInput.Close();
            _ = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            if (process.ExitCode != 0 || !output.Wait(2000)) return null;
            var environment = ParseEnvironmentBlock(output.Result);
            // Without a display this is not a graphical login's environment.
            return environment.ContainsKey("WAYLAND_DISPLAY") || environment.ContainsKey("DISPLAY") ? environment : null;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Reads "NAME=value" entries separated by NUL, leaving out what belongs to the unit that printed them.</summary>
    internal static Dictionary<string, string> ParseEnvironmentBlock(string block)
    {
        static bool OfThePrintingUnit(string key) => key is "INVOCATION_ID" or "SYSTEMD_EXEC_PID" or "JOURNAL_STREAM" or "NOTIFY_SOCKET"
            or "CREDENTIALS_DIRECTORY" or "PWD" or "OLDPWD" or "SHLVL" or "_"
            || key.StartsWith("MANAGERPID", StringComparison.Ordinal) || key.StartsWith("MEMORY_PRESSURE_", StringComparison.Ordinal)
            || key.StartsWith("WATCHDOG_", StringComparison.Ordinal) || key.StartsWith("LISTEN_", StringComparison.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in block.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator > 0 && !OfThePrintingUnit(entry[..separator])) result[entry[..separator]] = entry[(separator + 1)..];
        }

        return result;
    }

    // Without a service manager to ask, hand on the session variables this process itself was given.
    private static Dictionary<string, string> InheritedSessionEnvironment()
    {
        static bool Allowed(string key) => key is "PATH" or "LANG" or "LANGUAGE" or "SHELL" or "DISPLAY" or "WAYLAND_DISPLAY"
            or "DBUS_SESSION_BUS_ADDRESS" or "XAUTHORITY" or "DESKTOP_SESSION" or "GDMSESSION" or "GNOME_DESKTOP_SESSION_ID"
            or "XDG_CURRENT_DESKTOP" or "XDG_SESSION_DESKTOP" or "XDG_SESSION_TYPE" or "XDG_SESSION_CLASS" or "XDG_SESSION_ID"
            or "XDG_RUNTIME_DIR" or "XDG_MENU_PREFIX" or "XDG_DATA_DIRS" or "XDG_CONFIG_DIRS" or "XDG_DATA_HOME" or "XDG_CONFIG_HOME"
            or "XDG_CACHE_HOME" or "XDG_STATE_HOME" or "GDK_SCALE" or "GDK_DPI_SCALE" or "GTK_THEME" or "GTK_MODULES"
            or "XMODIFIERS" or "GTK_IM_MODULE" or "QT_IM_MODULE" or "QT_IM_MODULES" or "IM_CONFIG_ENTRY" or "SSH_AUTH_SOCK"
            or "ELECTRON_OZONE_PLATFORM_HINT" or "OZONE_PLATFORM" or "QT_QPA_PLATFORM"
            || key.StartsWith("LC_", StringComparison.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value && Allowed(key)) result[key] = value;
        return result;
    }

    private sealed class DesktopSession(WindowsPaths paths, string? executable, Action<string, WindowsPaths> launch) : IClaudeDesktopSession
    {
        // Remembered from the first stop, so a recovery that stops the app again still restarts it.
        private string? _executable = executable;
        private string Directory => paths.DesktopUserDataDirectory;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            using (var holder = Holder(Directory))
            {
                if (holder is not null)
                {
                    _executable ??= holder.Executable;
                    await holder.StopAsync(cancellationToken);
                }
            }

            // Renderers and helpers can outlive the main process for a moment and
            // may still be writing; the directory must not move under them.
            var usesDirectory = new Regex("--user-data-dir=" + Regex.Escape(Directory) + @"(?=\s|$)", RegexOptions.CultureInvariant);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (true)
            {
                var busy = false;
                foreach (var process in LinuxProcess.Enumerate())
                    using (process)
                        busy |= IsDesktopExecutable(process.Executable) && usesDirectory.IsMatch(string.Join(' ', process.Arguments));
                if (!busy) return;
                if (DateTime.UtcNow >= deadline)
                    throw new ClaudeSyncBarException("Claude 데스크톱 앱이 완전히 종료되지 않았습니다. 앱을 직접 종료한 뒤 다시 시도해 주세요.");
                await Task.Delay(200, cancellationToken);
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_executable is null) return;
            using (var running = Holder(Directory))
                if (running is not null) return;
            launch(_executable, paths);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var started = Holder(Directory))
                    if (started is not null) return;
                await Task.Delay(250, cancellationToken);
            }

            throw new ClaudeSyncBarException("Claude 데스크톱 앱이 다시 시작되지 않았습니다. 이전 상태를 복구합니다.");
        }

        public void Dispose() { }
    }
}
