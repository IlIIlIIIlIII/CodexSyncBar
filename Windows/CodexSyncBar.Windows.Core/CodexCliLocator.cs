namespace CodexSyncBar.Windows.Core;

internal static class CodexCliLocator
{
    // On Windows, an installed Codex desktop package can expose a codex.exe
    // before the official npm CLI shim. The shim is the command-line entry
    // point that supports the app-server contract used by SyncBar.
    private static readonly string[] CandidateNames = ["codex.cmd", "codex.exe", "codex"];

    public static string? Find()
    {
        // Packaged WinUI launches do not always inherit the user's npm bin
        // directory in PATH. npm's default Windows global bin is stable, so
        // check it explicitly before considering desktop-package codex.exe.
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var directory in new[]
        {
            Path.Combine(applicationData, "npm"),
            Path.Combine(localApplicationData, "npm"),
        })
        {
            var candidate = Path.Combine(directory, "codex.cmd");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return FindInPath(Environment.GetEnvironmentVariable("PATH"));
    }

    internal static string? FindInPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var name in CandidateNames)
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
