namespace ClaudeSyncBar.Windows.Core;

internal static class ClaudeCliLocator
{
    public static string? Find()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var executable = FindInPath(Environment.GetEnvironmentVariable("PATH"));
        if (executable is not null) return executable;
        foreach (var directory in new[] { Path.Combine(home, ".local", "bin"), "/usr/local/bin", "/usr/bin" })
            if (IsExecutable(Path.Combine(directory, "claude"))) return Path.Combine(directory, "claude");
        return FindManaged(new WindowsPaths()) ?? FindDesktopBundled(home);
    }
    internal static string? Find(string? path, string applicationData, string localApplicationData, string userProfile) =>
        FindInPath(path) ?? FindManaged(new WindowsPaths(userProfile, localApplicationData));
    internal static string ManagedExecutable(string versionDirectory) =>
        Path.Combine(versionDirectory, "node_modules", ".bin", "claude");
    internal static string? FindManaged(WindowsPaths paths)
    {
        var tools = Path.Combine(paths.ExternalRuntimeDirectory, "tools");
        var pointer = Path.Combine(tools, "current.txt");
        WindowsPathSafety.EnsurePrivateFile(pointer, "CLI 버전 선택", 1024);
        if (!File.Exists(pointer)) return null;
        var selected = File.ReadAllText(pointer).Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(selected, "^claude-code-[a-f0-9]{32}$")) return null;
        var executable = ManagedExecutable(Path.Combine(tools, selected));
        return IsExecutable(executable) ? executable : null;
    }
    // Claude Desktop ships the same CLI under a per-version directory and keeps older ones around.
    internal static string? FindDesktopBundled(string home)
    {
        var root = Path.Combine(home, ".config", "Claude", "claude-code");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root)
            .Select(directory => (Directory: directory, Version: Version.TryParse(Path.GetFileName(directory), out var version) ? version : null))
            .Where(item => item.Version is not null)
            .OrderByDescending(item => item.Version)
            .SelectMany(item => Directory.EnumerateDirectories(item.Directory).Select(build => Path.Combine(build, "claude")))
            .FirstOrDefault(IsExecutable);
    }
    internal static string? FindInPath(string? path)
    {
        foreach (var directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, "claude");
            if (IsExecutable(candidate)) return candidate;
        }
        return null;
    }
    internal static bool IsExecutable(string path)
    {
        try { return File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
