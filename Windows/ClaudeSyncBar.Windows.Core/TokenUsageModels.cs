using System.Text.Json.Serialization;

namespace ClaudeSyncBar.Windows.Core;

public sealed class TokenCounts
{
    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("cachedInputTokens")]
    public long CachedInputTokens { get; init; }

    [JsonPropertyName("cacheWriteInputTokens")]
    public long CacheWriteInputTokens { get; init; }

    [JsonPropertyName("cacheWriteOneHourInputTokens")]
    public long CacheWriteOneHourInputTokens { get; init; }

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
        CacheWriteOneHourInputTokens = first.CacheWriteOneHourInputTokens + second.CacheWriteOneHourInputTokens,
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

    [JsonPropertyName("cacheWriteOneHourInputTokens")]
    public long CacheWriteOneHourInputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("reasoningOutputTokens")]
    public long ReasoningOutputTokens { get; init; }

    [JsonPropertyName("totalTokens")]
    public long TotalTokens { get; init; }

    [JsonPropertyName("isLongContext")]
    public bool? IsLongContext { get; init; }

    [JsonPropertyName("requests")]
    public long Requests { get; init; }

    [JsonIgnore]
    public TokenCounts Counts => new()
    {
        InputTokens = InputTokens,
        CachedInputTokens = CachedInputTokens,
        CacheWriteInputTokens = CacheWriteInputTokens,
        CacheWriteOneHourInputTokens = CacheWriteOneHourInputTokens,
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

    [JsonPropertyName("cacheWriteOneHourInputTokens")]
    public long CacheWriteOneHourInputTokens { get; init; }

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

    [JsonPropertyName("windowDays")]
    public int? WindowDays { get; init; }

    [JsonPropertyName("windowStartedAt")]
    public string? WindowStartedAt { get; init; }

    /// <summary>The start of the earliest minute with tokens in the window; null when there is none.</summary>
    [JsonPropertyName("firstRecordAt")]
    public string? FirstRecordAt { get; init; }

    [JsonPropertyName("models")]
    public List<string> Models { get; init; } = [];

    [JsonPropertyName("tiers")]
    public List<string> Tiers { get; init; } = [];

    /// <summary>
    /// The same tokens by minute: [minute since the epoch, model index, tier index, long context 0/1, input,
    /// cached input, cache write, one-hour cache write, output, reasoning output, requests]. Schema 7 and later.
    /// </summary>
    [JsonPropertyName("minutes")]
    public List<long[]> Minutes { get; init; } = [];

    /// <summary>The sessions counted in the window, so another device's copy of them can be left out.</summary>
    [JsonPropertyName("sessions")]
    public List<string> Sessions { get; init; } = [];

    [JsonIgnore]
    public TokenCounts Counts => new()
    {
        InputTokens = InputTokens,
        CachedInputTokens = CachedInputTokens,
        CacheWriteInputTokens = CacheWriteInputTokens,
        CacheWriteOneHourInputTokens = CacheWriteOneHourInputTokens,
        OutputTokens = OutputTokens,
        ReasoningOutputTokens = ReasoningOutputTokens,
        TotalTokens = TotalTokens,
    };

    /// <summary>The minutes decoded; entries that do not fit the format are left out.</summary>
    public IReadOnlyList<TokenMinute> MinuteSeries()
    {
        var series = new List<TokenMinute>(Minutes.Count);
        foreach (var row in Minutes)
        {
            if (row is not { Length: >= 11 } || row[1] < 0 || row[1] >= Models.Count || row[2] < 0 || row[2] >= Tiers.Count) continue;
            series.Add(new TokenMinute(row[0], Models[(int)row[1]], Tiers[(int)row[2]], row[3] == 1,
                Math.Max(0, row[4]), Math.Max(0, row[5]), Math.Max(0, row[6]), Math.Max(0, row[7]), Math.Max(0, row[8]), Math.Max(0, row[9]), Math.Max(0, row[10])));
        }

        return series;
    }

    /// <summary>The 30-day summary without its minutes, for screens that show only totals.</summary>
    public DeviceTokenUsageSummary WithoutMinutes() => new()
    {
        SchemaVersion = SchemaVersion, GeneratedAt = GeneratedAt, ScannedFiles = ScannedFiles, Requests = Requests,
        InputTokens = InputTokens, CachedInputTokens = CachedInputTokens, CacheWriteInputTokens = CacheWriteInputTokens,
        CacheWriteOneHourInputTokens = CacheWriteOneHourInputTokens, OutputTokens = OutputTokens,
        ReasoningOutputTokens = ReasoningOutputTokens, TotalTokens = TotalTokens, Buckets = Buckets, Errors = Errors,
        WindowDays = WindowDays, WindowStartedAt = WindowStartedAt, FirstRecordAt = FirstRecordAt,
    };
}

/// <summary>One minute of one model's tokens on one device.</summary>
public sealed record TokenMinute(
    long Minute, string Model, string ServiceTier, bool IsLongContext, long InputTokens, long CachedInputTokens,
    long CacheWriteInputTokens, long CacheWriteOneHourInputTokens, long OutputTokens, long ReasoningOutputTokens, long Requests)
{
    public DateTimeOffset StartedAt => DateTimeOffset.FromUnixTimeMilliseconds(Minute * 60_000);
    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>Everything but cache reads: uncached input, cache writes and output.</summary>
    public long UncachedTokens => Math.Max(0, InputTokens - CachedInputTokens) + OutputTokens;

    public ModelTokenUsage Usage => new()
    {
        Model = Model, ServiceTier = ServiceTier, IsLongContext = IsLongContext, Requests = Requests, InputTokens = InputTokens,
        CachedInputTokens = CachedInputTokens, CacheWriteInputTokens = CacheWriteInputTokens,
        CacheWriteOneHourInputTokens = CacheWriteOneHourInputTokens, OutputTokens = OutputTokens,
        ReasoningOutputTokens = ReasoningOutputTokens, TotalTokens = TotalTokens,
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
    // Cache writes cost a multiple of the input rate that depends on how long they are kept.
    private const decimal FiveMinuteCacheWrite = 1.25m;
    private const decimal OneHourCacheWrite = 2m;

    /// <summary>USD per million tokens at Anthropic's API rates, for an API-equivalent estimate.</summary>
    private sealed record Rate(
        string CanonicalModel,
        decimal Input,
        decimal CachedInput,
        decimal Output,
        decimal? FastMultiplier = null,
        decimal LongContextMultiplier = 1m);

    // Only fast mode is billed above the standard rate; the priority service
    // tier is reserved capacity at the same per-token price.
    public static bool IsPriorityServiceTier(string tier) =>
        tier.Equals("fast", StringComparison.OrdinalIgnoreCase);

    public static TokenCostEstimate EstimateUsd(ModelTokenUsage usage)
    {
        var rate = FindRate(usage.Model);
        if (rate is null)
        {
            return new TokenCostEstimate(0m, false, null, 1m);
        }

        var multiplier = IsPriorityServiceTier(usage.ServiceTier)
            ? rate.FastMultiplier ?? 1m
            : 1m;
        var cached = Math.Clamp(usage.CachedInputTokens, 0, Math.Max(0, usage.InputTokens));
        var uncached = Math.Max(0, usage.InputTokens - cached);
        var writes = Math.Clamp(usage.CacheWriteInputTokens, 0, uncached);
        var oneHourWrites = Math.Clamp(usage.CacheWriteOneHourInputTokens, 0, writes);
        // The higher rate card applies to the whole request, cached parts included.
        var rateCard = usage.IsLongContext == true ? rate.LongContextMultiplier : 1m;
        var baseCost = ((uncached - writes) * rate.Input
            + (writes - oneHourWrites) * rate.Input * FiveMinuteCacheWrite
            + oneHourWrites * rate.Input * OneHourCacheWrite
            + cached * rate.CachedInput
            + Math.Max(0, usage.OutputTokens) * rate.Output) * rateCard / 1_000_000m;
        return new TokenCostEstimate(
            baseCost * multiplier,
            true,
            rate.CanonicalModel,
            multiplier);
    }

    // Model ids may carry a date suffix or a provider prefix, so they are
    // matched by family. Models without a published rate stay unpriced.
    private static Rate? FindRate(string rawModel)
    {
        var model = rawModel.ToLowerInvariant();
        if (model.Contains("claude-fable-5-1", StringComparison.Ordinal)
            || model.Contains("claude-mythos-5-1", StringComparison.Ordinal))
        {
            return new Rate("Claude Fable 5.1", 10m, .25m, 50m);
        }

        if (model.Contains("claude-fable-5", StringComparison.Ordinal)
            || model.Contains("claude-mythos-5", StringComparison.Ordinal))
        {
            return new Rate("Claude Fable 5", 10m, 1m, 50m);
        }

        if (model.Contains("claude-opus-5-5", StringComparison.Ordinal))
        {
            return new Rate("Claude Opus 5.5", 4m, .2m, 20m, FastMultiplier: 2m);
        }

        if (model.Contains("claude-opus-5", StringComparison.Ordinal))
        {
            return new Rate("Claude Opus 5", 5m, .5m, 25m, FastMultiplier: 2m);
        }

        if (model.Contains("claude-opus-4-8", StringComparison.Ordinal)
            || model.Contains("claude-opus-4-7", StringComparison.Ordinal)
            || model.Contains("claude-opus-4-6", StringComparison.Ordinal))
        {
            return new Rate("Claude Opus 4", 5m, .5m, 25m);
        }

        if (model.Contains("claude-sonnet-5", StringComparison.Ordinal))
        {
            return new Rate("Claude Sonnet 5", 2m, .2m, 10m);
        }

        if (model.Contains("claude-sonnet-4-6", StringComparison.Ordinal))
        {
            return new Rate("Claude Sonnet 4.6", 3m, .3m, 15m);
        }

        if (model.Contains("claude-haiku-5-5", StringComparison.Ordinal))
        {
            // Prompts above 100K tokens are billed at five times the base rate card.
            return new Rate("Claude Haiku 5.5", .1m, .01m, .5m, LongContextMultiplier: 5m);
        }

        if (model.Contains("claude-haiku-4-5", StringComparison.Ordinal))
        {
            return new Rate("Claude Haiku 4.5", 1m, .1m, 5m);
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
