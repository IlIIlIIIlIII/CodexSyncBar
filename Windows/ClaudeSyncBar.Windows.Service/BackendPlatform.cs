using System.Diagnostics;
using ClaudeSyncBar.Windows.Core;
using Microsoft.Win32;

namespace ClaudeSyncBar.Ubuntu.Backend;

/// <summary>
/// What the shared management service needs from the desktop it runs on: Windows.
/// The Ubuntu backend compiles its own file of the same name.
/// </summary>
internal static class BackendPlatform
{
    /// <summary>How this computer is named in the device list.</summary>
    public const string LocalDeviceName = "이 PC";

    /// <summary>The shared core already names this platform in its messages.</summary>
    public const string Name = "Windows";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ClaudeSyncBar";

    /// <summary>The app shows notifications; the service only hands them over.</summary>
    public static Action<string>? Notifier { get; set; }

    /// <summary>The program started at sign-in. The app sets it to its own executable.</summary>
    public static string? LaunchCommand { get; set; }

    // An isolated home is a test's: it keeps the choice in a file instead of the user's registry.
    private static string IsolatedFlag(WindowsPaths paths) => Path.Combine(paths.StateRoot, "launch-at-login");

    public static bool LaunchAtLogin(WindowsPaths paths)
    {
        if (paths.IsIsolated) return File.Exists(IsolatedFlag(paths));
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string { Length: > 0 };
    }

    public static void SetLaunchAtLogin(WindowsPaths paths, bool enabled)
    {
        if (paths.IsIsolated)
        {
            WindowsPathSafety.EnsurePrivateDirectory(paths.StateRoot, "SyncBar 상태 디렉터리");
            if (enabled) File.WriteAllText(IsolatedFlag(paths), "");
            else File.Delete(IsolatedFlag(paths));
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new ClaudeSyncBarException("Windows 시작 프로그램 설정을 열지 못했습니다.");
        if (!enabled)
        {
            key.DeleteValue(RunValue, throwOnMissingValue: false);
            return;
        }

        var executable = LaunchCommand ?? Environment.ProcessPath
            ?? throw new ClaudeSyncBarException("실행 파일 경로를 확인하지 못했습니다.");
        key.SetValue(RunValue, $"\"{executable}\" --background", RegistryValueKind.String);
    }

    public static void Notify(string message)
    {
        try { Notifier?.Invoke(message); }
        catch (Exception) { /* The result is recorded in the app either way. */ }
    }

    /// <summary>
    /// Whether the session is still open somewhere: Claude Desktop names the
    /// session it runs, a terminal session is known by its working directory.
    /// </summary>
    public static bool SessionStillOpen(StoppedSession session)
    {
        foreach (var process in WindowsProcess.Enumerate())
            using (process)
            {
                if (!process.IsClaudeCli) continue;
                if (process.Arguments.Any(argument => argument.Contains(session.SessionId, StringComparison.OrdinalIgnoreCase))) return true;
                // A process whose directory cannot be read may be this session; it is not touched.
                if (process.CurrentDirectory is not { } directory) return true;
                if (string.Equals(Path.TrimEndingDirectorySeparator(directory), Path.TrimEndingDirectorySeparator(session.Cwd), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

        return false;
    }

    /// <summary>Runs the resume command in the background, without a window, independent of this app.</summary>
    public static bool LaunchResume(StoppedSession session, IReadOnlyList<string> command, WindowsPaths paths)
    {
        var program = command[0];
        var arguments = command.Skip(1).ToList();
        // The npm shim is a command script; its package is run with node directly, so no shell parses the prompt.
        if (program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            if (NpmEntryPoint(program) is not { } script || FindOnPath("node.exe") is not { } node) return false;
            arguments.Insert(0, script);
            program = node;
        }

        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = session.Cwd };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // A session started from inside another Claude session must not inherit its token or host.
        foreach (var name in start.Environment.Keys.Where(key => key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) || key.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        if (paths.IsIsolated) start.Environment.Remove("CLAUDE_CONFIG_DIR");
        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string? NpmEntryPoint(string shim)
    {
        var directory = Path.GetDirectoryName(shim)!;
        foreach (var root in new[] { directory, Path.GetDirectoryName(Path.GetDirectoryName(directory)) ?? directory })
        {
            var candidate = Path.Combine(root, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static string? FindOnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(directory => Path.Combine(directory.Trim().Trim('"'), name))
        .FirstOrDefault(File.Exists);
}
