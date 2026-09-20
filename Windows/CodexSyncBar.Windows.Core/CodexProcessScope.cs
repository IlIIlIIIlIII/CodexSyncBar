using System.Text;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

public enum CodexProcessTarget { NotAppServer, CurrentHome, OtherHome, Unknown }

/// <summary>Only an explicit absolute Unix socket argument proves a native process's auth home.</summary>
public static class CodexProcessScope
{
    public static CodexProcessTarget Classify(string processName, string commandLine, string codexHome)
    {
        if (!LocalSwitchService.IsCodexAppServerCommandLine(processName, commandLine)) return CodexProcessTarget.NotAppServer;
        if (processName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("codex.cmd", StringComparison.OrdinalIgnoreCase))
            return CodexProcessTarget.Unknown; // Never terminate a shell based on text embedded in its command.

        var arguments = SplitCommandLine(commandLine);
        if (arguments.Count == 0) return CodexProcessTarget.Unknown;
        if (arguments.Any(argument => argument is "--help" or "-h" or "--version")) return CodexProcessTarget.NotAppServer;
        var index = 1;
        if (processName.Equals("node.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Count < 2 || FileName(arguments[1]) is not ("codex.js" or "codex")) return CodexProcessTarget.NotAppServer;
            index = 2;
        }
        else if (!processName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase)
            || FileName(arguments[0]) != "codex.exe") return CodexProcessTarget.Unknown;

        while (index < arguments.Count && arguments[index].StartsWith('-'))
        {
            if (arguments[index] is "-c" or "--config" or "--enable" or "--disable" or "-p" or "--profile") index += 2;
            else if (arguments[index].StartsWith("--config=", StringComparison.Ordinal)) index++;
            else return CodexProcessTarget.Unknown;
        }
        if (index >= arguments.Count || arguments[index] != "app-server") return CodexProcessTarget.NotAppServer;
        index++;
        var proxy = index < arguments.Count && arguments[index] == "proxy";
        if (proxy) index++;
        var sockets = new List<string>();
        while (index < arguments.Count)
        {
            var argument = arguments[index++];
            if (argument is "-c" or "--config" or "--enable" or "--disable") { index++; continue; }
            if (argument is "--sock" or "--listen")
            {
                if (index >= arguments.Count) return CodexProcessTarget.Unknown;
                if (argument == "--sock" && proxy) sockets.Add(arguments[index]);
                else if (argument == "--listen" && arguments[index].StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
                    sockets.Add(arguments[index][7..]);
                index++;
            }
            else if (argument.StartsWith("--sock=", StringComparison.Ordinal) && proxy) sockets.Add(argument[7..]);
            else if (argument.StartsWith("--listen=unix://", StringComparison.OrdinalIgnoreCase)) sockets.Add(argument[16..]);
        }
        if (sockets.Count != 1) return CodexProcessTarget.Unknown;
        var socket = NormalizeAbsolutePath(sockets[0]);
        var home = NormalizeAbsolutePath(codexHome);
        if (socket is null || home is null) return CodexProcessTarget.Unknown;
        return socket.StartsWith(home.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
            ? CodexProcessTarget.CurrentHome : CodexProcessTarget.OtherHome;
    }

    private static string FileName(string value) => value.Replace('\\', '/').Split('/').Last().ToLowerInvariant();

    private static string? NormalizeAbsolutePath(string value)
    {
        var path = value.Replace('\\', '/');
        if (Regex.IsMatch(path, "^/[a-zA-Z]:/")) path = path[1..]; // unix:///C:/Users/...
        if (path.Contains('%') || path.Contains('$') || path.StartsWith("//?/") || path.StartsWith("//./")) return null;
        string prefix;
        string remainder;
        if (Regex.IsMatch(path, "^[a-zA-Z]:/")) { prefix = path[..2]; remainder = path[3..]; }
        else if (path.StartsWith("//"))
        {
            var parts = path[2..].Split('/', 3);
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
            prefix = "//" + parts[0] + "/" + parts[1];
            remainder = parts.Length == 3 ? parts[2] : "";
        }
        else return null;
        var segments = new List<string>();
        foreach (var part in remainder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (segments.Count == 0) return null; segments.RemoveAt(segments.Count - 1); }
            else
            {
                // Avoid Win32 aliases (trailing dots/spaces), ADS paths and ambiguous literal quotes.
                if (part.EndsWith('.') || part.EndsWith(' ') || part.Contains(':') || part.Contains('"')) return null;
                segments.Add(part);
            }
        }
        return prefix + "/" + string.Join('/', segments);
    }

    internal static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        for (var index = 0; index < commandLine.Length; index++)
        {
            var character = commandLine[index];
            if (character == '\\')
            {
                var start = index;
                while (index < commandLine.Length && commandLine[index] == '\\') index++;
                var count = index - start;
                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1) current.Append('"'); else quoted = !quoted;
                }
                else { current.Append('\\', count); index--; }
                started = true;
            }
            else if (character == '"') { quoted = !quoted; started = true; }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (started) { result.Add(current.ToString()); current.Clear(); started = false; }
            }
            else { current.Append(character); started = true; }
        }
        if (quoted) return []; // Unterminated quoting cannot prove the path.
        if (started) result.Add(current.ToString());
        return result;
    }
}
