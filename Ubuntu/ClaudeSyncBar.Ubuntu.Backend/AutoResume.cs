using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Ubuntu.Backend;

/// <summary>A Claude Code session whose last turn the service refused for its usage limit.</summary>
internal sealed record StoppedSession(string SessionId, string Cwd, string ErrorId, DateTimeOffset StoppedAt)
{
    // Known only for sessions Claude Desktop keeps: how the session ran there.
    public string? Model { get; init; }
    public string? PermissionMode { get; init; }
}

/// <summary>How many times a session was resumed after which stop.</summary>
public sealed record ResumeRecord(string ErrorId, int Count, DateTimeOffset LastAt);

/// <summary>
/// Finds sessions stopped by a usage limit and continues them in the
/// background once the account in use has room again. Claude Code ends such a
/// turn with a synthetic assistant record that carries the service's status,
/// and `claude --resume id --print prompt` continues the same transcript.
/// </summary>
internal static class AutoResume
{
    internal const string Prompt = "사용량 한도 때문에 중단됐던 작업을 이어서 진행해 주세요.";
    internal static readonly TimeSpan Window = TimeSpan.FromHours(24);
    internal const int MaximumPerDay = 3;
    private const int TailBytes = 256 * 1024;
    private static readonly Regex SessionName = new("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> PermissionModes = ["default", "acceptEdits", "plan", "auto", "bypassPermissions"];

    /// <summary>The stop at the end of a transcript, or null when its last turn did not end on a usage limit.</summary>
    internal static StoppedSession? Read(string transcript, DateTimeOffset since)
    {
        string[] lines;
        try
        {
            using var stream = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            stream.ReadExactly(buffer);
            lines = Encoding.UTF8.GetString(buffer).Split('\n');
            // A cut at the start of the tail leaves a partial first line.
            if (start > 0) lines = lines[1..];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }

        for (var index = lines.Length - 1; index >= 0; index--)
        {
            if (lines[index].Length == 0) continue;
            JsonElement record;
            try { record = JsonDocument.Parse(lines[index]).RootElement; }
            catch (JsonException) { continue; }
            if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("type", out var type)) continue;
            // Bookkeeping records follow the turn; the last message is what counts.
            if (type.GetString() is not ("user" or "assistant")) continue;
            if (type.GetString() != "assistant" || !UsageLimit(record)) return null;
            if (!record.TryGetProperty("sessionId", out var session) || session.GetString() is not { } sessionId || !SessionName.IsMatch(sessionId)
                || !record.TryGetProperty("cwd", out var cwd) || cwd.GetString() is not { Length: > 0 } directory || !Path.IsPathFullyQualified(directory)
                || !record.TryGetProperty("uuid", out var uuid) || uuid.GetString() is not { Length: > 0 } errorId
                || !record.TryGetProperty("timestamp", out var stamp) || !DateTimeOffset.TryParse(stamp.GetString(), out var at))
                return null;
            return at >= since ? new(sessionId, directory, errorId, at) : null;
        }

        return null;
    }

    // Refused for a limit: HTTP 429, or an error the service names as a limit.
    private static bool UsageLimit(JsonElement record) =>
        record.TryGetProperty("isApiErrorMessage", out var flag) && flag.ValueKind == JsonValueKind.True
        && (record.TryGetProperty("apiErrorStatus", out var status) && status.ValueKind == JsonValueKind.Number && status.GetInt32() == 429
            || record.TryGetProperty("error", out var kind) && kind.ValueKind == JsonValueKind.String
               && kind.GetString()!.Contains("limit", StringComparison.OrdinalIgnoreCase));

    /// <summary>Stopped sessions of this Claude Code configuration, most recent first.</summary>
    internal static IReadOnlyList<StoppedSession> Find(WindowsPaths paths, DateTimeOffset now)
    {
        var projects = Path.Combine(paths.ClaudeHome, "projects");
        if (!Directory.Exists(projects)) return [];
        var desktop = DesktopSessions(paths);
        var since = now - Window;
        var found = new List<StoppedSession>();
        foreach (var file in Directory.EnumerateFiles(projects, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (File.GetLastWriteTimeUtc(file) < since.UtcDateTime || !SessionName.IsMatch(Path.GetFileNameWithoutExtension(file))) continue;
            if (Read(file, since) is not { } stopped || stopped.SessionId != Path.GetFileNameWithoutExtension(file)) continue;
            found.Add(desktop.TryGetValue(stopped.SessionId, out var how) ? stopped with { Model = how.Model, PermissionMode = how.PermissionMode } : stopped);
        }

        return found.OrderByDescending(session => session.StoppedAt).ToArray();
    }

    // Claude Desktop records how each of its sessions runs; a resumed run keeps to it.
    private static Dictionary<string, (string? Model, string? PermissionMode)> DesktopSessions(WindowsPaths paths)
    {
        var result = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        var root = Path.Combine(paths.DesktopUserDataDirectory, "claude-code-sessions");
        if (!Directory.Exists(root)) return result;
        foreach (var file in Directory.EnumerateFiles(root, "local_*.json", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 4 * 1024 * 1024) continue;
                using var document = JsonDocument.Parse(File.ReadAllBytes(file));
                var record = document.RootElement;
                if (!record.TryGetProperty("cliSessionId", out var id) || id.GetString() is not { } session) continue;
                var model = record.TryGetProperty("model", out var m) && m.GetString() is { } value && Regex.IsMatch(value, "^[A-Za-z0-9.:_\\[\\]-]{1,80}$") ? value : null;
                var mode = record.TryGetProperty("permissionMode", out var p) && p.GetString() is { } permission && PermissionModes.Contains(permission) ? permission : null;
                result[session] = (model, mode);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }

        return result;
    }

    /// <summary>
    /// Whether the session is still open somewhere: Claude Desktop names the
    /// session it runs, a terminal session is known by its working directory.
    /// Writing to a session from two places would split its transcript.
    /// </summary>
    internal static bool StillOpen(StoppedSession session) => BackendPlatform.SessionStillOpen(session);

    /// <summary>Continues the session in the background, outside this service, as the user.</summary>
    internal static bool Launch(StoppedSession session, WindowsPaths paths)
    {
        if (ClaudeCliLocator.Find() is not { } claude || !Directory.Exists(session.Cwd)) return false;
        var command = new List<string> { claude, "--resume", session.SessionId, "--print", Prompt, "--output-format", "text" };
        if (session.Model is { } model) command.AddRange(["--model", model]);
        if (session.PermissionMode is { } mode) command.AddRange(["--permission-mode", mode]);
        return BackendPlatform.LaunchResume(session, command, paths);
    }
}
