using System.Text.Json.Serialization;

namespace CodexSyncBar.Windows.Core;

public sealed class TokenCounts
{
    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("cachedInputTokens")]
    public long CachedInputTokens { get; init; }

    [JsonPropertyName("cacheWriteInputTokens")]
    public long CacheWriteInputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("reasoningOutputTokens")]
    public long ReasoningOutputTokens { get; init; }

    [JsonPropertyName("totalTokens")]
    public long TotalTokens { get; init; }

    public static TokenCounts operator +(TokenCounts first, TokenCounts second) => new()
    {
        InputTokens = first.InputTokens + second.InputTokens,
        CachedInputTokens = first.CachedInputTokens + second.CachedInputTokens,
        CacheWriteInputTokens = first.CacheWriteInputTokens + second.CacheWriteInputTokens,
        OutputTokens = first.OutputTokens + second.OutputTokens,
        ReasoningOutputTokens = first.ReasoningOutputTokens + second.ReasoningOutputTokens,
        TotalTokens = first.TotalTokens + second.TotalTokens,
    };
}

public sealed class ModelTokenUsage
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = "unknown";

    [JsonPropertyName("serviceTier")]
    public string ServiceTier { get; init; } = "default";

    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("cachedInputTokens")]
    public long CachedInputTokens { get; init; }

    [JsonPropertyName("cacheWriteInputTokens")]
    public long CacheWriteInputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("reasoningOutputTokens")]
    public long ReasoningOutputTokens { get; init; }

    [JsonPropertyName("totalTokens")]
    public long TotalTokens { get; init; }

    [JsonPropertyName("requests")]
    public long Requests { get; init; }

    [JsonIgnore]
    public TokenCounts Counts => new()
    {
        InputTokens = InputTokens,
        CachedInputTokens = CachedInputTokens,
        CacheWriteInputTokens = CacheWriteInputTokens,
        OutputTokens = OutputTokens,
        ReasoningOutputTokens = ReasoningOutputTokens,
        TotalTokens = TotalTokens,
    };
}

public sealed class DeviceTokenUsageSummary
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; init; } = string.Empty;

    [JsonPropertyName("scannedFiles")]
    public int ScannedFiles { get; init; }

    [JsonPropertyName("requests")]
    public long Requests { get; init; }

    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("cachedInputTokens")]
    public long CachedInputTokens { get; init; }

    [JsonPropertyName("cacheWriteInputTokens")]
    public long CacheWriteInputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("reasoningOutputTokens")]
    public long ReasoningOutputTokens { get; init; }

    [JsonPropertyName("totalTokens")]
    public long TotalTokens { get; init; }

    [JsonPropertyName("buckets")]
    public List<ModelTokenUsage> Buckets { get; init; } = [];

    [JsonPropertyName("errors")]
    public List<string> Errors { get; init; } = [];

    [JsonIgnore]
    public TokenCounts Counts => new()
    {
        InputTokens = InputTokens,
        CachedInputTokens = CachedInputTokens,
        CacheWriteInputTokens = CacheWriteInputTokens,
        OutputTokens = OutputTokens,
        ReasoningOutputTokens = ReasoningOutputTokens,
        TotalTokens = TotalTokens,
    };
}

public sealed class DeviceTokenUsage
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public bool IsReachable { get; init; }
    public DeviceTokenUsageSummary? Summary { get; init; }
    public string? Error { get; init; }

    public decimal EstimatedCostUsd => Summary?.Buckets.Sum(bucket =>
        TokenUsagePricing.EstimateUsd(bucket).PricedUsd) ?? 0m;

    public long UnpricedTokens => Summary?.Buckets.Sum(bucket =>
        TokenUsagePricing.EstimateUsd(bucket).IsPriced ? 0 : bucket.TotalTokens) ?? 0;

    public long PriorityPricedTokens => Summary?.Buckets.Sum(bucket =>
        TokenUsagePricing.IsPriorityServiceTier(bucket.ServiceTier)
            ? bucket.TotalTokens
            : 0) ?? 0;
}

public sealed class TokenUsageSnapshot
{
    public required IReadOnlyList<DeviceTokenUsage> Devices { get; init; }
    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.UtcNow;

    public int ReachableDeviceCount => Devices.Count(item => item.IsReachable && item.Summary is not null);
    public int TotalDeviceCount => Devices.Count;
    public TokenCounts Counts => Devices
        .Where(item => item.Summary is not null)
        .Select(item => item.Summary!.Counts)
        .Aggregate(new TokenCounts(), (first, second) => first + second);
    public decimal EstimatedCostUsd => Devices.Sum(item => item.EstimatedCostUsd);
    public long UnpricedTokens => Devices.Sum(item => item.UnpricedTokens);
    public long PriorityPricedTokens => Devices.Sum(item => item.PriorityPricedTokens);
}

public sealed record TokenCostEstimate(
    decimal PricedUsd,
    bool IsPriced,
    string? CanonicalModel,
    decimal Multiplier);

public static class TokenUsagePricing
{
    private sealed record Rate(
        string CanonicalModel,
        decimal Input,
        decimal CachedInput,
        decimal Output,
        decimal? PriorityMultiplier);

    public static bool IsPriorityServiceTier(string tier) =>
        tier.Equals("priority", StringComparison.OrdinalIgnoreCase)
        || tier.Equals("fast", StringComparison.OrdinalIgnoreCase);

    public static TokenCostEstimate EstimateUsd(ModelTokenUsage usage)
    {
        var rate = FindRate(usage.Model);
        if (rate is null)
        {
            return new TokenCostEstimate(0m, false, null, 1m);
        }

        var multiplier = IsPriorityServiceTier(usage.ServiceTier)
            ? rate.PriorityMultiplier ?? 1m
            : 1m;
        var cached = Math.Clamp(usage.CachedInputTokens, 0, usage.InputTokens);
        var uncached = Math.Max(0, usage.InputTokens - cached);
        var baseCost = (uncached * rate.Input
            + cached * rate.CachedInput
            + Math.Max(0, usage.OutputTokens) * rate.Output) / 1_000_000m;
        return new TokenCostEstimate(
            baseCost * multiplier,
            true,
            rate.CanonicalModel,
            multiplier);
    }

    private static Rate? FindRate(string rawModel)
    {
        var model = rawModel.ToLowerInvariant();
        if (model == "codex-auto-review"
            || (model.Contains("gpt-5.3-codex", StringComparison.Ordinal)
                && !model.Contains("spark", StringComparison.Ordinal)))
        {
            return new Rate("GPT-5.3-Codex", 1.75m, .175m, 14m, 2m);
        }

        if (model.Contains("gpt-5.3-codex-spark", StringComparison.Ordinal)
            || model.Contains("spark", StringComparison.Ordinal))
        {
            return null;
        }

        if (model.Contains("gpt-5.6-terra", StringComparison.Ordinal))
        {
            return new Rate("GPT-5.6 Terra", 2.5m, .25m, 15m, 2m);
        }

        if (model.Contains("gpt-5.6-luna", StringComparison.Ordinal))
        {
            return new Rate("GPT-5.6 Luna", 1m, .1m, 6m, 2m);
        }

        if (model.Contains("gpt-5.6", StringComparison.Ordinal))
        {
            return new Rate("GPT-5.6 Sol", 5m, .5m, 30m, 2m);
        }

        if (model.Contains("cyber", StringComparison.Ordinal))
        {
            return null;
        }

        if (model.Contains("gpt-5.5", StringComparison.Ordinal))
        {
            return new Rate("GPT-5.5", 5m, .5m, 30m, 2.5m);
        }

        if (model.Contains("gpt-5.4-mini", StringComparison.Ordinal))
        {
            return new Rate("GPT-5.4 Mini", .75m, .075m, 4.5m, 2m);
        }

        if (model.Contains("gpt-5.4", StringComparison.Ordinal))
        {
            return new Rate("GPT-5.4", 2.5m, .25m, 15m, 2m);
        }

        if (model.Contains("gpt-5.2", StringComparison.Ordinal))
        {
            return new Rate("GPT-5.2", 1.75m, .175m, 14m, 2m);
        }

        return null;
    }
}

public static class TokenUsageFormatting
{
    public static string Tokens(long count)
    {
        if (count >= 1_000_000_000) return $"{count / 1_000_000_000d:0.00}B";
        if (count >= 1_000_000) return $"{count / 1_000_000d:0.00}M";
        if (count >= 1_000) return $"{count / 1_000d:0.0}K";
        return count.ToString();
    }

    public static string Dollars(decimal value)
    {
        var format = value >= 100m ? "#,##0" : value >= 10m ? "#,##0.0" : "#,##0.00";
        return $"${value.ToString(format, System.Globalization.CultureInfo.InvariantCulture)}";
    }
}
