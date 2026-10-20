using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeSyncBar.App.Model;

/// <summary>
/// The concrete host aliases in the user's OpenSSH config. OpenSSH itself
/// resolves their options; the same rules as the Ubuntu app's ssh_config.py.
/// </summary>
public static partial class SshConfig
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,252}$")]
    private static partial Regex HostPattern();

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    /// <summary>Every alias a Host line names, without wildcards, following Include lines.</summary>
    public static List<string> Hosts(string? config = null)
    {
        var path = Path.GetFullPath(config ?? DefaultPath);
        var root = Path.GetDirectoryName(path)!;
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Read(string file, int depth)
        {
            var full = Path.GetFullPath(file);
            if (depth > 16 || !visited.Add(full) || !File.Exists(full)) return;
            var number = 0;
            foreach (var line in File.ReadLines(full))
            {
                number++;
                var parts = Split(line) ?? throw new FormatException($"SSH config 구문을 확인해 주세요: {Path.GetFileName(full)}:{number}");
                if (parts.Count == 0) continue;
                // OpenSSH takes both "Host alias" and "Host=alias".
                var (key, values) = parts[0].IndexOf('=') is var equal and > 0
                    ? (parts[0][..equal], new[] { parts[0][(equal + 1)..] }.Where(value => value.Length > 0).Concat(parts.Skip(1)).ToList())
                    : (parts[0], parts.Skip(1).SkipWhile(value => value == "=").ToList());
                if (key.Equals("host", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var alias in values.Where(alias => HostPattern().IsMatch(alias))) aliases.TryAdd(alias, alias);
                }
                else if (key.Equals("include", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var pattern in values)
                    {
                        var include = pattern.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + pattern[1..] : pattern;
                        if (!Path.IsPathRooted(include)) include = Path.Combine(root, include);
                        var directory = Path.GetDirectoryName(include)!;
                        if (!Directory.Exists(directory)) continue;
                        foreach (var match in Directory.GetFiles(directory, Path.GetFileName(include)).Order(StringComparer.Ordinal)) Read(match, depth + 1);
                    }
                }
            }
        }

        Read(path, 0);
        return [.. aliases.Values];
    }

    /// <summary>A new device for the alias, with the user and port OpenSSH resolves for it.</summary>
    public static JsonObject Resolve(string alias)
    {
        if (!HostPattern().IsMatch(alias)) throw new FormatException("SSH 호스트 이름을 확인해 주세요.");
        var start = new ProcessStartInfo(SshExecutable())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-G", "-o", "PermitLocalCommand=no", alias }) start.ArgumentList.Add(argument);
        string output;
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException();
            var reading = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                throw new TimeoutException();
            }

            output = reading.Result;
            if (process.ExitCode != 0) throw new InvalidOperationException();
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or IOException)
        {
            throw new FormatException("SSH 설정을 읽지 못했어요. ssh config 구문과 OpenSSH 클라이언트 설치를 확인해 주세요.");
        }

        var options = output.Split('\n').Select(line => line.TrimEnd('\r').Split(' ', 2)).Where(pair => pair.Length == 2)
            .GroupBy(pair => pair[0]).ToDictionary(group => group.Key, group => group.First()[1]);
        if (!options.TryGetValue("user", out var user) || user.Length == 0 || !options.TryGetValue("port", out var port) || !int.TryParse(port, out var number))
            throw new FormatException("SSH 설정에서 사용자 이름과 포트를 확인하지 못했어요.");
        return new JsonObject
        {
            ["displayName"] = alias.Length > 64 ? alias[..64] : alias, ["host"] = alias, ["username"] = user, ["port"] = number,
            ["authentication"] = "openSSHConfig", ["enabled"] = false,
        };
    }

    // Windows ships the OpenSSH client; one on PATH is used when it is not installed.
    private static string SshExecutable()
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        return File.Exists(system) ? system : "ssh.exe";
    }

    // Splits a config line into words as OpenSSH does: quotes group, # starts a comment. Null when a quote is left open.
    private static List<string>? Split(string line)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var character in line)
        {
            if (quoted)
            {
                if (character == '"') quoted = false;
                else word.Append(character);
                continue;
            }

            if (character == '#' && !started) break;
            if (char.IsWhiteSpace(character))
            {
                if (started) words.Add(word.ToString());
                word.Clear();
                started = false;
                continue;
            }

            started = true;
            if (character == '"') quoted = true;
            else word.Append(character);
        }

        if (quoted) return null;
        if (started) words.Add(word.ToString());
        return words;
    }
}
