using CodexSyncBar.Windows.Core;
using System.Text.Json;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class TokenUsageTests
{
    [Fact]
    public async Task LocalHelperRebuildsThenAtomicallyUpdatesCacheOutsideVirtualizedAppData()
    {
        var root = Path.Combine(Path.GetTempPath(), "syncbar usage cache " + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new WindowsPaths(Path.Combine(root, "home"), Path.Combine(root, "local"));
            paths.EnsureDirectories();
            // A cache from the previous location must be disposable. Never
            // migrate or overwrite it while moving off the virtualized path.
            var previousCache = Path.Combine(paths.StateRoot, "usage-cache.json");
            const string previousContents = "old cache left untouched";
            await File.WriteAllTextAsync(previousCache, previousContents);
            Assert.Equal("..", Path.GetRelativePath(paths.LocalAppData, paths.UsageCacheFile)
                .Split(Path.DirectorySeparatorChar)[0]);

            var sessions = Path.Combine(paths.CodexHome, "sessions");
            Directory.CreateDirectory(sessions);
            var session = Path.Combine(sessions, "isolated.jsonl");
            await File.WriteAllTextAsync(session,
                "{\"type\":\"session_meta\",\"payload\":{\"id\":\"isolated-session\"}}\n" +
                "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5.2\"}}\n" +
                TokenEvent(100, 100));

            var service = new TokenUsageService(paths);
            var initial = await service.FetchLocalAsync(CancellationToken.None);
            Assert.Null(initial.Error);
            Assert.True(initial.IsReachable);
            Assert.Equal(100, initial.Summary!.TotalTokens);
            Assert.True(File.Exists(paths.UsageCacheFile));

            await File.AppendAllTextAsync(session, TokenEvent(50, 150));
            var updated = await service.FetchLocalAsync(CancellationToken.None);
            Assert.Null(updated.Error);
            Assert.Equal(150, updated.Summary!.TotalTokens);
            Assert.Equal(2, updated.Summary.Requests);

            var cached = await service.FetchLocalAsync(CancellationToken.None);
            Assert.Null(cached.Error);
            Assert.Equal(150, cached.Summary!.TotalTokens);
            using var cache = JsonDocument.Parse(await File.ReadAllTextAsync(paths.UsageCacheFile));
            Assert.Equal(6, cache.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Empty(Directory.GetFiles(paths.ExternalRuntimeDirectory, "usage-cache.json.*.tmp"));
            Assert.Equal(previousContents, await File.ReadAllTextAsync(previousCache));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static string TokenEvent(int last, int total) => JsonSerializer.Serialize(new
    {
        timestamp = DateTimeOffset.UtcNow.ToString("O"), type = "event_msg",
        payload = new
        {
            type = "token_count",
            info = new
            {
                last_token_usage = new { input_tokens = last, output_tokens = 0, total_tokens = last },
                total_token_usage = new { input_tokens = total, output_tokens = 0, total_tokens = total },
            },
        },
    }) + "\n";

    [Fact]
    public void TokenSummaryAndPricingMatchSwiftContract()
    {
        const string json = """
            {
              "schemaVersion": 6,
              "generatedAt": "2030-01-01T00:00:00.000Z",
              "scannedFiles": 2,
              "requests": 3,
              "inputTokens": 1000000,
              "cachedInputTokens": 200000,
              "cacheWriteInputTokens": 0,
              "outputTokens": 100000,
              "reasoningOutputTokens": 0,
              "totalTokens": 1100000,
              "buckets": [
                {
                  "model": "gpt-5.2",
                  "serviceTier": "default",
                  "inputTokens": 1000000,
                  "cachedInputTokens": 200000,
                  "cacheWriteInputTokens": 0,
                  "outputTokens": 100000,
                  "reasoningOutputTokens": 0,
                  "totalTokens": 1100000,
                  "requests": 3
                }
              ],
              "errors": []
            }
            """;

        var summary = TokenUsageService.ParseSummary($"warning\n{json}\n");
        var estimate = TokenUsagePricing.EstimateUsd(summary.Buckets.Single());

        Assert.Equal(6, summary.SchemaVersion);
        Assert.Equal(1_100_000, summary.TotalTokens);
        Assert.True(estimate.IsPriced);
        Assert.Equal(2.835m, estimate.PricedUsd);
        Assert.Equal("$2.84", TokenUsageFormatting.Dollars(estimate.PricedUsd));
        Assert.Equal("$1,235", TokenUsageFormatting.Dollars(1234.56m));
    }

}
