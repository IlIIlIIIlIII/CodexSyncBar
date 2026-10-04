namespace CodexSyncBar.Windows.Core;

internal static class CodexCliLocator
{
    public static string? Find()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var executable = FindInPath(Environment.GetEnvironmentVariable("PATH"));
        if (executable is not null) return executable;
        foreach (var directory in new[] { Path.Combine(home, ".local", "bin"), "/usr/local/bin", "/usr/bin" })
            if (IsExecutable(Path.Combine(directory, "codex"))) return Path.Combine(directory, "codex");
        return FindManaged(new WindowsPaths()) ?? (IsExecutable("/usr/lib/chatgpt/resources/codex") ? "/usr/lib/chatgpt/resources/codex" : null);
    }
    internal static string? Find(string? path, string applicationData, string localApplicationData, string userProfile) =>
        FindInPath(path) ?? FindManaged(new WindowsPaths(userProfile, localApplicationData));
    internal static string? FindManaged(WindowsPaths paths)
    {
        var tools = Path.Combine(paths.ExternalRuntimeDirectory, "tools");
        var pointer = Path.Combine(tools, "current.txt");
        WindowsPathSafety.EnsurePrivateFile(pointer, "CLI 버전 선택", 1024);
        if (!File.Exists(pointer)) return null;
        var selected = File.ReadAllText(pointer).Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(selected, "^rust-v[0-9]+\\.[0-9]+\\.[0-9]+-[a-f0-9]{32}$")) return null;
        var executable = Path.Combine(tools, selected, "codex");
        return IsExecutable(executable) ? executable : null;
    }
    internal static string? FindInPath(string? path)
    {
        foreach (var directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, "codex");
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
