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
        Assert.Equal(6, summary.SchemaVersion);
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
}
