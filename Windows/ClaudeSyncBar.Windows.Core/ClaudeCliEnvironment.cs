namespace ClaudeSyncBar.Windows.Core;

internal static class ClaudeCliEnvironment
{
    /// <summary>
    /// Environment for a Claude CLI that must use only the given config
    /// directory. A CLI started from inside another Claude session would
    /// otherwise inherit that session's token, endpoint and host-managed login.
    /// </summary>
    public static Dictionary<string, string?> Isolated(string configDirectory)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in Environment.GetEnvironmentVariables().Keys.OfType<string>())
            if (name.StartsWith("CLAUDE_CODE_", StringComparison.Ordinal)
                || name.StartsWith("ANTHROPIC_", StringComparison.Ordinal)
                || name is "CLAUDECODE" or "CLAUDE_CONFIG_DIR")
                environment[name] = null;
        environment["CLAUDE_CONFIG_DIR"] = configDirectory;
        environment["NO_COLOR"] = "1";
        return environment;
    }
}
