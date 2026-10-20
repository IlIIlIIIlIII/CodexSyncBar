using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeSyncBar.Windows.Core;

/// <summary>
/// The 30-day token summary of Support/usage-summary.mjs, computed in this
/// process for a computer without Node.js, as Windows usually is. It reads
/// Claude Code's session logs, counts each response once and remembers in its
/// own cache how far each log was read. Keep the two in step.
/// </summary>
internal sealed class TokenUsageSummarizer(string transcriptsRoot, string cachePath)
{
    private const int SchemaVersion = 6;
    private const int WindowDays = 30;
    // Some models bill a prompt above this size on a higher rate card.
    private const long LongContextTokens = 100_000;
    private const string Native = "native";
    private static readonly byte[] AssistantMark = "\"assistant\""u8.ToArray();
    private static readonly byte[] UsageMark = "\"usage\""u8.ToArray();
    private static readonly JsonSerializerOptions CacheFormat = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DeviceTokenUsageSummary Summarize(CancellationToken token = default)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var cutoff = (double)generatedAt.AddDays(-WindowDays).ToUnixTimeMilliseconds();
        var cache = LoadCache();
        var files = TranscriptFiles();
        var relatives = files.Select(file => Path.GetRelativePath(transcriptsRoot, file)).ToList();
        var present = relatives.ToHashSet(StringComparer.Ordinal);
        foreach (var gone in cache.Files.Keys.Where(relative => !present.Contains(relative)).ToList()) cache.Files.Remove(gone);
        // Which log first counted each response; a resumed session copies earlier responses into its new file.
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (relative, state) in cache.Files)
            foreach (var key in state.Responses.Keys) owners.TryAdd(key, relative);

        var errors = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            try { cache.Files[relatives[i]] = Scan(files[i], relatives[i], cache.Files.GetValueOrDefault(relatives[i]), cutoff, owners); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errors.Add($"{relatives[i]}: {error.Message}"); }
        }

        SaveCache(cache);
        var merged = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        foreach (var bucket in cache.Files.Values.SelectMany(state => state.Buckets.Values))
        {
            if (Time(bucket.StartedAt) is not { } started || started < cutoff) continue;
            var key = $"{bucket.Model}\u001f{bucket.ServiceTier}\u001f{(bucket.IsLongContext ? "true" : "false")}";
            if (!merged.TryGetValue(key, out var sum))
                merged[key] = sum = new Bucket { Model = bucket.Model, ServiceTier = bucket.ServiceTier, IsLongContext = bucket.IsLongContext };
            sum.Add(bucket);
            sum.Requests += Math.Max(0, bucket.Requests);
        }

        var buckets = merged.Values.OrderByDescending(bucket => bucket.TotalTokens).ToList();
        var total = new Bucket();
        foreach (var bucket in buckets)
        {
            total.Add(bucket);
            total.Requests += bucket.Requests;
        }

        return new DeviceTokenUsageSummary
        {
            SchemaVersion = SchemaVersion,
            GeneratedAt = generatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ScannedFiles = files.Count,
            Requests = total.Requests,
            InputTokens = total.InputTokens,
            CachedInputTokens = total.CachedInputTokens,
            CacheWriteInputTokens = total.CacheWriteInputTokens,
            CacheWriteOneHourInputTokens = total.CacheWriteOneHourInputTokens,
            OutputTokens = total.OutputTokens,
            ReasoningOutputTokens = total.ReasoningOutputTokens,
            TotalTokens = total.TotalTokens,
            Buckets = buckets.Select(bucket => new ModelTokenUsage
            {
                Model = bucket.Model, ServiceTier = bucket.ServiceTier, IsLongContext = bucket.IsLongContext, Requests = bucket.Requests,
                InputTokens = bucket.InputTokens, CachedInputTokens = bucket.CachedInputTokens, CacheWriteInputTokens = bucket.CacheWriteInputTokens,
                CacheWriteOneHourInputTokens = bucket.CacheWriteOneHourInputTokens, OutputTokens = bucket.OutputTokens,
                ReasoningOutputTokens = bucket.ReasoningOutputTokens, TotalTokens = bucket.TotalTokens,
            }).ToList(),
            Errors = errors.Take(20).ToList(),
        };
    }

    private List<string> TranscriptFiles()
    {
        if (!Directory.Exists(transcriptsRoot)) return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return Directory.EnumerateFiles(transcriptsRoot, "*.jsonl", options).Order(StringComparer.Ordinal).ToList();
    }

    private static FileState Scan(string file, string relative, FileState? previous, double cutoff, Dictionary<string, string> owners)
    {
        var info = new FileInfo(file);
        var size = info.Length;
        var modified = Milliseconds(info.LastWriteTimeUtc);
        // A log replaced under the same name is read again from the start.
        var identity = info.CreationTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        var state = previous is { Dev: Native } kept && kept.Ino == identity && kept.Offset >= 0 && kept.Offset <= size
            ? kept : FileState.Empty(identity, size, modified);
        if (state.Offset == size && state.MtimeMs == modified)
        {
            Prune(state, cutoff);
            return state;
        }

        if (state.Offset == size) state = FileState.Empty(identity, size, modified);
        if (state.Offset == 0)
        {
            // Read again from the start: what it claimed before is forgotten.
            foreach (var key in owners.Where(owner => owner.Value == relative).Select(owner => owner.Key).ToList()) owners.Remove(key);
        }

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(state.Offset, SeekOrigin.Begin);
        var consumed = state.Offset;
        var pending = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var chunk = buffer.AsSpan(0, read);
            int newline;
            while ((newline = chunk.IndexOf((byte)'\n')) >= 0)
            {
                pending.Write(chunk[..newline]);
                var line = pending.GetBuffer().AsMemory(0, (int)pending.Length);
                consumed += pending.Length + 1;
                Line(state, relative, line, modified, cutoff, owners);
                pending.SetLength(0);
                chunk = chunk[(newline + 1)..];
            }

            pending.Write(chunk);
        }

        // A line still being written stays unread until its newline arrives.
        state.Offset = Math.Min(consumed, size);
        state.Size = size;
        state.MtimeMs = modified;
        Prune(state, cutoff);
        return state;
    }

    private static void Line(FileState state, string relative, ReadOnlyMemory<byte> line, double fallback, double cutoff, Dictionary<string, string> owners)
    {
        if (line.Span.IndexOf(AssistantMark) < 0 || line.Span.IndexOf(UsageMark) < 0) return;
        try
        {
            using var document = JsonDocument.Parse(line);
            Event(state, relative, document.RootElement, fallback, cutoff, owners);
        }
        catch (JsonException)
        {
            // A single malformed event must not discard the rest of a session.
        }
    }

    private static void Event(FileState state, string relative, JsonElement entry, double fallback, double cutoff, Dictionary<string, string> owners)
    {
        if (entry.ValueKind != JsonValueKind.Object || Text(entry, "type") != "assistant") return;
        if (!entry.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
        if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return;
        // Locally generated notices carry a placeholder model and no real usage.
        if (Text(message, "model") is not { Length: > 0 } model || model == "<synthetic>") return;
        if (Text(message, "id") is not { Length: > 0 } id) return;
        var key = ResponseKey(id, Text(entry, "requestId") ?? "");
        if (owners.ContainsKey(key)) return;
        var occurred = Time(Text(entry, "timestamp")) ?? fallback;
        if (occurred < cutoff) return;
        owners[key] = relative;
        state.Responses[key] = occurred;
        var tokens = Tokens(usage);
        if (tokens.TotalTokens <= 0) return;
        var started = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(occurred / 60_000) * 60_000).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var tier = Tier(usage);
        var longContext = tokens.InputTokens > LongContextTokens;
        var bucketKey = $"{started}\u001f{model}\u001f{tier}\u001f{(longContext ? "true" : "false")}";
        if (!state.Buckets.TryGetValue(bucketKey, out var bucket))
            state.Buckets[bucketKey] = bucket = new Bucket { StartedAt = started, Model = model, ServiceTier = tier, IsLongContext = longContext };
        bucket.Add(tokens);
        bucket.Requests += 1;
    }

    // Claude reports uncached, cache-read and cache-write prompt tokens apart.
    // The summary's input is the whole prompt with the cached parts as subsets of it,
    // which is what the cost estimate expects; hour-long cache writes cost more.
    private static Bucket Tokens(JsonElement usage)
    {
        var cached = Count(usage, "cache_read_input_tokens");
        var cacheWrite = Count(usage, "cache_creation_input_tokens");
        var input = Count(usage, "input_tokens") + cached + cacheWrite;
        var output = Count(usage, "output_tokens");
        var hour = usage.TryGetProperty("cache_creation", out var creation) && creation.ValueKind == JsonValueKind.Object
            ? Count(creation, "ephemeral_1h_input_tokens") : 0;
        var thinking = usage.TryGetProperty("output_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
            ? Count(details, "thinking_tokens") : 0;
        return new Bucket
        {
            InputTokens = input, CachedInputTokens = cached, CacheWriteInputTokens = cacheWrite,
            CacheWriteOneHourInputTokens = Math.Min(cacheWrite, hour), OutputTokens = output,
            ReasoningOutputTokens = Math.Min(output, thinking), TotalTokens = input + output,
        };
    }

    private static string Tier(JsonElement usage)
    {
        if (Text(usage, "speed") == "fast") return "fast";
        return Text(usage, "service_tier") is { Length: > 0 } tier && tier != "standard" ? tier : "default";
    }

    private static string ResponseKey(string id, string request)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(id + "\u001f" + request));
        return Convert.ToBase64String(digest, 0, 9).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static void Prune(FileState state, double cutoff)
    {
        foreach (var key in state.Buckets.Where(pair => !(Time(pair.Value.StartedAt) >= cutoff)).Select(pair => pair.Key).ToList()) state.Buckets.Remove(key);
        foreach (var key in state.Responses.Where(pair => !(pair.Value >= cutoff)).Select(pair => pair.Key).ToList()) state.Responses.Remove(key);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // A non-negative whole number JavaScript holds exactly; anything else counts as none.
    private static long Count(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            && number is >= 0 and <= 9_007_199_254_740_991 ? number : 0;

    private static double? Time(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time.ToUnixTimeMilliseconds() : null;

    private static double Milliseconds(DateTime utc) => (utc - DateTime.UnixEpoch).TotalMilliseconds;

    private Cache LoadCache()
    {
        try
        {
            if (File.Exists(cachePath)
                && JsonSerializer.Deserialize<Cache>(File.ReadAllBytes(cachePath), CacheFormat) is { SchemaVersion: SchemaVersion, Files: not null } cache)
                return cache;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // A missing or broken cache is rebuilt from the logs.
        }

        return new Cache { SchemaVersion = SchemaVersion };
    }

    private void SaveCache(Cache cache)
    {
        var temporary = cachePath + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            WindowsPathSafety.WritePrivateBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(cache, CacheFormat));
            File.Move(temporary, cachePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed class Cache
    {
        public int SchemaVersion { get; set; }
        public Dictionary<string, FileState> Files { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class FileState
    {
        public string Dev { get; set; } = "";
        public string Ino { get; set; } = "";
        public long Offset { get; set; }
        public long Size { get; set; }
        public double MtimeMs { get; set; }
        public Dictionary<string, double> Responses { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, Bucket> Buckets { get; set; } = new(StringComparer.Ordinal);

        public static FileState Empty(string identity, long size, double modified) =>
            new() { Dev = Native, Ino = identity, Offset = 0, Size = size, MtimeMs = modified };
    }

    private sealed class Bucket
    {
        public string StartedAt { get; set; } = "";
        public bool IsLongContext { get; set; }
        public string Model { get; set; } = "";
        public string ServiceTier { get; set; } = "";
        public long InputTokens { get; set; }
        public long CachedInputTokens { get; set; }
        public long CacheWriteInputTokens { get; set; }
        public long CacheWriteOneHourInputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long ReasoningOutputTokens { get; set; }
        public long TotalTokens { get; set; }
        public long Requests { get; set; }

        public void Add(Bucket other)
        {
            InputTokens += Math.Max(0, other.InputTokens);
            CachedInputTokens += Math.Max(0, other.CachedInputTokens);
            CacheWriteInputTokens += Math.Max(0, other.CacheWriteInputTokens);
            CacheWriteOneHourInputTokens += Math.Max(0, other.CacheWriteOneHourInputTokens);
            OutputTokens += Math.Max(0, other.OutputTokens);
            ReasoningOutputTokens += Math.Max(0, other.ReasoningOutputTokens);
            TotalTokens += Math.Max(0, other.TotalTokens);
        }
    }
}
