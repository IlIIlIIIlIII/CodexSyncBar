using System.Text.Json;
using ClaudeSyncBar.Ubuntu.Backend;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Ubuntu.Tests;

/// <summary>
/// Reading Claude Code transcripts for sessions a usage limit stopped. The
/// records copy the shape Claude Code 2.1.295 writes when the service refuses a turn.
/// </summary>
public sealed class AutoResumeTests : IDisposable
{
    private const string Session = "f3d8e206-f07a-45b9-8b1c-5cc7f829bae0";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-resume-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private WindowsPaths Paths => new(Path.Combine(_root, "home"), Path.Combine(_root, "data"));

    private static string Line(object record) => JsonSerializer.Serialize(record);

    private string User(string text, double minutesAgo, string session = Session) => Line(new
    {
        type = "user", sessionId = session, cwd = "/work/project", uuid = Guid.NewGuid().ToString(),
        timestamp = _now.AddMinutes(-minutesAgo).ToString("O"), message = new { role = "user", content = text },
    });

    private string Refused(int status, string error, double minutesAgo, string uuid = "error-1", string session = Session) => Line(new
    {
        type = "assistant", sessionId = session, cwd = "/work/project", uuid, timestamp = _now.AddMinutes(-minutesAgo).ToString("O"),
        error, isApiErrorMessage = true, apiErrorStatus = status,
        message = new { role = "assistant", model = "<synthetic>", content = new[] { new { type = "text", text = "API Error" } } },
    });

    private static string Bookkeeping(string type) => Line(new { type });

    private string Transcript(params string[] lines)
    {
        var file = Path.Combine(_root, "transcripts", Guid.NewGuid().ToString("N") + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Join('\n', lines) + "\n");
        return file;
    }

    [Fact]
    public void TurnRefusedForTheUsageLimitIsAStopEvenWithBookkeepingAfterIt()
    {
        var file = Transcript(User("작업해 줘", 30),
            Line(new { type = "system", subtype = "api_error", level = "error" }),
            Refused(429, "rate_limit", 20), Bookkeeping("last-prompt"), Bookkeeping("cost-state"));
        var stopped = AutoResume.Read(file, _now.AddHours(-24));
        Assert.NotNull(stopped);
        Assert.Equal((Session, "/work/project", "error-1"), (stopped!.SessionId, stopped.Cwd, stopped.ErrorId));
        // The service may name the limit without the status.
        Assert.NotNull(AutoResume.Read(Transcript(User("a", 9), Refused(400, "usage_limit_reached", 8)), _now.AddHours(-24)));
    }

    [Fact]
    public void OtherEndingsAreNotStopsToContinue()
    {
        var since = _now.AddHours(-24);
        // The user went on after the stop.
        Assert.Null(AutoResume.Read(Transcript(Refused(429, "rate_limit", 20), User("다른 일", 10)), since));
        // Refused for another reason, such as a login that is no longer valid.
        Assert.Null(AutoResume.Read(Transcript(User("a", 9), Refused(401, "authentication_failed", 8)), since));
        // A normal answer.
        Assert.Null(AutoResume.Read(Transcript(User("a", 9), Line(new { type = "assistant", sessionId = Session, cwd = "/work/project",
            uuid = "ok", timestamp = _now.ToString("O"), message = new { role = "assistant" } })), since));
        // Too long ago to pick up unasked.
        Assert.Null(AutoResume.Read(Transcript(Refused(429, "rate_limit", 60 * 25)), since));
        // A record that cannot be trusted to name a session and a directory.
        Assert.Null(AutoResume.Read(Transcript(Line(new { type = "assistant", isApiErrorMessage = true, apiErrorStatus = 429,
            sessionId = "../escape", cwd = "relative", uuid = "x", timestamp = _now.ToString("O") })), since));
        Assert.Null(AutoResume.Read(Transcript("{ not json", ""), since));
    }

    [Fact]
    public void OnlyTheEndOfALongTranscriptIsRead()
    {
        var filler = User(new string('x', 4000), 50);
        var file = Transcript([.. Enumerable.Repeat(filler, 200), User("a", 9), Refused(429, "rate_limit", 8)]);
        Assert.True(new FileInfo(file).Length > 512 * 1024);
        Assert.NotNull(AutoResume.Read(file, _now.AddHours(-24)));
    }

    [Fact]
    public void SessionsAreFoundInThisConfigurationAndKeepHowClaudeDesktopRanThem()
    {
        var project = Path.Combine(Paths.ClaudeHome, "projects", "-work-project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, Session + ".jsonl"), string.Join('\n', User("a", 9), Refused(429, "rate_limit", 8)) + "\n");
        const string terminal = "0cf2be08-405e-495f-8569-d3a0c56bf000";
        File.WriteAllText(Path.Combine(project, terminal + ".jsonl"), string.Join('\n', User("b", 30, terminal), Refused(429, "rate_limit", 5, "error-2", terminal)) + "\n");
        // A file named for one session that holds another is not trusted.
        File.WriteAllText(Path.Combine(project, "11111111-2222-4333-8444-555555555555.jsonl"), Refused(429, "rate_limit", 5) + "\n");
        var records = Path.Combine(Paths.DesktopUserDataDirectory, "claude-code-sessions", "account", "org");
        Directory.CreateDirectory(records);
        File.WriteAllText(Path.Combine(records, "local_1.json"), Line(new { cliSessionId = Session, model = "claude-opus-5-5", permissionMode = "bypassPermissions" }));
        File.WriteAllText(Path.Combine(records, "local_2.json"), Line(new { cliSessionId = terminal, model = "$(touch x)", permissionMode = "anything" }));

        var found = AutoResume.Find(Paths, _now);
        Assert.Equal(new[] { terminal, Session }, found.Select(stop => stop.SessionId));
        Assert.Equal(("claude-opus-5-5", "bypassPermissions"), (found[1].Model, found[1].PermissionMode));
        // Values that are not a model name or a permission mode are dropped, not passed on.
        Assert.Equal((null, null), (found[0].Model, found[0].PermissionMode));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
