using System.Text.Json.Nodes;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Windows.Tests;

/// <summary>The in-process token summary follows Support/usage-summary.mjs.</summary>
public sealed class TokenUsageSummarizerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-tokens-" + Guid.NewGuid().ToString("N"));
    private string Projects => Path.Combine(_root, "projects");
    private string Cache => Path.Combine(_root, "cache.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Assistant(string id, string request, string model, DateTimeOffset at, long input, long output,
        long cacheRead = 0, long cacheWrite = 0, long hour = 0, long thinking = 0, string? tier = null, string? speed = null)
    {
        var usage = new JsonObject
        {
            ["input_tokens"] = input, ["output_tokens"] = output, ["cache_read_input_tokens"] = cacheRead,
            ["cache_creation_input_tokens"] = cacheWrite, ["cache_creation"] = new JsonObject { ["ephemeral_1h_input_tokens"] = hour },
            ["output_tokens_details"] = new JsonObject { ["thinking_tokens"] = thinking },
        };
        if (tier is not null) usage["service_tier"] = tier;
        if (speed is not null) usage["speed"] = speed;
        return new JsonObject
        {
            ["type"] = "assistant", ["requestId"] = request, ["timestamp"] = at.UtcDateTime.ToString("o"),
            ["message"] = new JsonObject { ["id"] = id, ["model"] = model, ["usage"] = usage },
        }.ToJsonString();
    }

    private string Log(string name, params string[] lines)
    {
        var path = Path.Combine(Projects, "-C-work", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    [Fact]
    public void EachResponseCountsOnceByTheHelpersRules()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var first = Assistant("msg_1", "req_1", "claude-opus-5-5", now, 100, 50, cacheRead: 1000, cacheWrite: 500, hour: 200, thinking: 20);
        Log("a.jsonl",
            first,
            first, // the same response written again
            new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["usage"] = "assistant" } }.ToJsonString(),
            Assistant("msg_s", "req_s", "<synthetic>", now, 10, 10),
            Assistant("msg_2", "req_2", "claude-sonnet-5-5", now, 10, 5, speed: "fast"),
            Assistant("msg_old", "req_old", "claude-opus-5-5", now.AddDays(-40), 999, 999),
            Assistant("msg_4", "req_4", "claude-opus-5-5", now, 150_000, 10),
            "{\"type\":\"assistant\",\"usage\": broken");
        // A resumed session copies an earlier response into its own file.
        Log("b.jsonl", first, Assistant("msg_5", "req_5", "claude-haiku-5-5", now, 1, 1, tier: "priority"));

        var summary = new TokenUsageSummarizer(Projects, Cache).Summarize();
        Assert.Equal(7, summary.SchemaVersion);
        Assert.Equal(2, summary.ScannedFiles);
        Assert.Equal(4, summary.Requests);
        Assert.Empty(summary.Errors);
        var opus = Assert.Single(summary.Buckets, b => b.Model == "claude-opus-5-5" && b.IsLongContext == false);
        Assert.Equal((1600L, 1000L, 500L, 200L, 50L, 20L, 1650L, 1L),
            (opus.InputTokens, opus.CachedInputTokens, opus.CacheWriteInputTokens, opus.CacheWriteOneHourInputTokens,
             opus.OutputTokens, opus.ReasoningOutputTokens, opus.TotalTokens, opus.Requests));
        Assert.Equal(150_010, Assert.Single(summary.Buckets, b => b.IsLongContext == true).TotalTokens);
        Assert.Equal("fast", Assert.Single(summary.Buckets, b => b.Model == "claude-sonnet-5-5").ServiceTier);
        Assert.Equal("priority", Assert.Single(summary.Buckets, b => b.Model == "claude-haiku-5-5").ServiceTier);
        Assert.Equal(1650 + 15 + 150_010 + 2, summary.TotalTokens);
        // Largest first, as the helper orders them.
        Assert.Equal(summary.Buckets.OrderByDescending(b => b.TotalTokens).Select(b => b.Model), summary.Buckets.Select(b => b.Model));
    }

    [Fact]
    public void OnlyWhatWasAddedIsReadAndAnUnfinishedLineWaits()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var path = Log("a.jsonl", Assistant("msg_1", "req_1", "claude-opus-5-5", now, 100, 10));
        var summarizer = new TokenUsageSummarizer(Projects, Cache);
        Assert.Equal(1, summarizer.Summarize().Requests);

        // Claude Code is still writing the next line.
        File.AppendAllText(path, Assistant("msg_2", "req_2", "claude-opus-5-5", now, 100, 10));
        Assert.Equal(1, summarizer.Summarize().Requests);
        File.AppendAllText(path, "\n");
        Assert.Equal(2, summarizer.Summarize().Requests);
        Assert.Equal(2, new TokenUsageSummarizer(Projects, Cache).Summarize().Requests);

        // A log written anew, shorter than what was read, is read again from the start.
        File.WriteAllText(path, Assistant("msg_3", "req_3", "claude-opus-5-5", now, 1, 1) + "\n");
        var again = summarizer.Summarize();
        Assert.Equal(1, again.Requests);
        Assert.Equal(2, again.TotalTokens);

        // A log that is gone takes its counts with it.
        File.Delete(path);
        Assert.Equal(0, summarizer.Summarize().Requests);
    }

    [Fact]
    public void AResponsesLaterLinesCountItsLargestOutputOnce()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var path = Log("a.jsonl",
            Assistant("msg_1", "req_1", "claude-opus-5-5", now, 100, 10, cacheRead: 1000, thinking: 4),
            Assistant("msg_1", "req_1", "claude-opus-5-5", now.AddSeconds(3), 100, 25, cacheRead: 1000, thinking: 9));
        var summarizer = new TokenUsageSummarizer(Projects, Cache);
        var summary = summarizer.Summarize();
        Assert.Equal((1100L, 25L, 9L, 1125L, 1L), (summary.InputTokens, summary.OutputTokens, summary.ReasoningOutputTokens, summary.TotalTokens, summary.Requests));

        // The rest of the response arrives after the log was read; its copy in another log changes nothing.
        File.AppendAllText(path, Assistant("msg_1", "req_1", "claude-opus-5-5", now.AddSeconds(6), 100, 40, cacheRead: 1000, thinking: 12) + "\n");
        Log("b.jsonl", Assistant("msg_1", "req_1", "claude-opus-5-5", now, 100, 99, cacheRead: 1000));
        var grown = summarizer.Summarize();
        Assert.Equal((40L, 12L, 1140L, 1L), (grown.OutputTokens, grown.ReasoningOutputTokens, grown.TotalTokens, grown.Requests));
        Assert.Equal(grown.TotalTokens, new TokenUsageSummarizer(Projects, Cache).Summarize().TotalTokens);

        // The minutes hold the same tokens, by minute and model.
        var minute = Assert.Single(grown.MinuteSeries());
        Assert.Equal(("claude-opus-5-5", 1100L, 40L, 12L), (minute.Model, minute.InputTokens, minute.OutputTokens, minute.ReasoningOutputTokens));
        Assert.Equal((long)Math.Floor(now.ToUnixTimeMilliseconds() / 60_000d), minute.Minute);
        Assert.Equal(minute.StartedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), grown.FirstRecordAt);
        Assert.Equal(7, grown.SchemaVersion);
    }

    [Theory]
    [InlineData("-home-me-app/0ac79c84.jsonl", "0ac79c84")]
    [InlineData("-home-me-app/0ac79c84/subagents/agent-1.jsonl", "0ac79c84")]
    [InlineData("ssh-0ac79c84/workflow-agent-a1.jsonl", "0ac79c84")]
    [InlineData("loose.jsonl", "loose")]
    public void ALogBelongsToItsSessionWhereverItWasWritten(string relative, string session) =>
        Assert.Equal(session, TokenUsageSummarizer.SessionOf(relative));

    [Fact]
    public void ASessionAnotherDeviceCountedIsLeftOut()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        Log("s1.jsonl", Assistant("msg_1", "req_1", "claude-opus-5-5", now, 100, 10));
        var copy = Path.Combine(Projects, "ssh-s2", "s2.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllText(copy, Assistant("msg_2", "req_2", "claude-opus-5-5", now, 1000, 10) + "\n");

        var all = new TokenUsageSummarizer(Projects, Cache).Summarize();
        Assert.Equal(1120, all.TotalTokens);
        Assert.Equal(["s1", "s2"], all.Sessions);
        var rest = new TokenUsageSummarizer(Projects, Cache).Summarize(skipSessions: new HashSet<string> { "s2" });
        Assert.Equal(110, rest.TotalTokens);
        Assert.Equal(["s1"], rest.Sessions);
        Assert.Equal(110, rest.MinuteSeries().Sum(minute => minute.TotalTokens));
    }

    [Fact]
    public void ReasoningIsTheLargestThinkingWithinTheLargestOutput()
    {
        // As the helper counts it: thinking above a line's own output still counts once the output grows.
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        Log("a.jsonl",
            Assistant("msg_t", "req_t", "claude-opus-5-5", now, 1, 10, thinking: 30),
            Assistant("msg_t", "req_t", "claude-opus-5-5", now, 1, 25, thinking: 20));
        var summary = new TokenUsageSummarizer(Projects, Cache).Summarize();
        Assert.Equal((25L, 25L), (summary.OutputTokens, summary.ReasoningOutputTokens));
    }
}
