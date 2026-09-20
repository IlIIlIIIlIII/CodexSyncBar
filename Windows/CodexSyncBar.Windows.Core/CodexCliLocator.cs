namespace CodexSyncBar.Windows.Core;

internal static class CodexCliLocator
{
    // On Windows, an installed Codex desktop package can expose a codex.exe
    // before the official npm CLI shim. The shim is the command-line entry
    // point that supports the app-server contract used by SyncBar.
    private static readonly string[] CandidateNames = ["codex.cmd", "codex.exe", "codex"];

    public static string? Find() => Find(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string? Find(string? path, string applicationData, string localApplicationData, string userProfile)
    {
        // Retain the user's official npm CLI preference, including custom PATH
        // shims, before using SyncBar's separately installed official binary.
        foreach (var directory in new[]
        {
            Path.Combine(applicationData, "npm"),
            Path.Combine(localApplicationData, "npm"),
        })
        {
            var candidate = Path.Combine(directory, "codex.cmd");
            if (File.Exists(candidate)) return candidate;
        }
        var pathShim = FindInPath(path, ["codex.cmd"]);
        if (pathShim is not null) return pathShim;

        // A desktop package's internal executable may reject app-server/login
        // when launched outside that package. Use the reviewed standalone CLI
        // before generic codex.exe candidates without modifying global PATH.
        var tools = Path.Combine(userProfile, ".codex-syncbar", "Tools");
        var pointer = Path.Combine(tools, "current.txt");
        if (File.Exists(pointer))
        {
            var selected = File.ReadAllText(pointer).Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(selected, "^rust-v[0-9]+\\.[0-9]+\\.[0-9]+-[a-f0-9]{32}$"))
            {
                var current = Path.Combine(tools, selected, "codex.exe");
                if (File.Exists(current)) return current;
            }
        }
        var standalone = Path.Combine(tools, "codex.exe");
        if (File.Exists(standalone)) return standalone;
        return FindInPath(path);
    }

    internal static string? FindInPath(string? path) => FindInPath(path, CandidateNames);

    private static string? FindInPath(string? path, IReadOnlyList<string> candidateNames)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var name in candidateNames)
        {
            foreach (var directoryValue in path.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = directoryValue.Trim().Trim('"');
                if (directory.Length == 0)
                {
                    continue;
                }

                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
