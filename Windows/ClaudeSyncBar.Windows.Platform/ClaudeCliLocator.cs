namespace ClaudeSyncBar.Windows.Core;

/// <summary>
/// Finds the Claude Code CLI on Windows: one on PATH (the native claude.exe or
/// the npm shim), the native installer's copy, the one this app installed, and
/// last the copy Claude Desktop keeps for itself.
/// </summary>
internal static class ClaudeCliLocator
{
    private static readonly string[] CommandNames = ["claude.exe", "claude.cmd"];

    public static string? Find()
    {
        var paths = new WindowsPaths();
        return FindInPath(Environment.GetEnvironmentVariable("PATH"))
            ?? Existing(NativeExecutable(paths.Home))
            ?? FindManaged(paths)
            ?? FindDesktopBundled(paths.DesktopBundledCliDirectory);
    }

    internal static string? Find(string? path, string applicationData, string localApplicationData, string userProfile) =>
        FindInPath(path) ?? FindManaged(new WindowsPaths(userProfile, localApplicationData));

    internal static string NativeExecutable(string home) => Path.Combine(home, ".local", "bin", "claude.exe");

    internal static string ManagedExecutable(string versionDirectory) =>
        Path.Combine(versionDirectory, "node_modules", ".bin", "claude.cmd");

    internal static string? FindManaged(WindowsPaths paths)
    {
        var tools = Path.Combine(paths.ExternalRuntimeDirectory, "tools");
        var pointer = Path.Combine(tools, "current.txt");
        WindowsPathSafety.EnsurePrivateFile(pointer, "CLI 버전 선택", 1024);
        if (!File.Exists(pointer)) return null;
        var selected = File.ReadAllText(pointer).Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(selected, "^claude-code-[a-f0-9]{32}$")) return null;
        return Existing(ManagedExecutable(Path.Combine(tools, selected)));
    }

    // Claude Desktop keeps the CLI under a per-version folder and leaves older ones in place.
    internal static string? FindDesktopBundled(string root)
    {
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root)
            .Select(directory => (Directory: directory, Version: Version.TryParse(Path.GetFileName(directory), out var version) ? version : null))
            .Where(item => item.Version is not null)
            .OrderByDescending(item => item.Version)
            .SelectMany(item => Directory.EnumerateDirectories(item.Directory).Select(build => Path.Combine(build, "claude.exe")))
            .FirstOrDefault(IsExecutable);
    }

    internal static string? FindInPath(string? path)
    {
        foreach (var name in CommandNames)
            foreach (var value in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = value.Trim().Trim('"');
                if (!Path.IsPathFullyQualified(directory)) continue;
                var candidate = Path.Combine(directory, name);
                if (IsExecutable(candidate)) return candidate;
            }

        return null;
    }

    private static string? Existing(string path) => IsExecutable(path) ? path : null;

    internal static bool IsExecutable(string path)
    {
        try { return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.Directory) == 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
