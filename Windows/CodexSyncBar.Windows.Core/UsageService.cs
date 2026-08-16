using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSyncBar.Windows.Core;

public sealed class UsageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly object _resetCreditsGate = new();
    private readonly Dictionary<string, ResetCreditsRequestState> _resetCreditsStates = [];

    public UsageService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(25);
    }

    public async Task<UsageSnapshot> FetchAsync(
        ProfileCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var payload = await GetAsync<UsagePayload>(
            "https://chatgpt.com/backend-api/wham/usage",
            credentials,
            cancellationToken);

        var resetCredits = await ResolveResetCreditsAsync(
            payload.RateLimitResetCredits,
            credentials,
            cancellationToken);

        var mainWindows = NormalizeWindows(payload.RateLimit);
        var spark = payload.AdditionalRateLimits?.FirstOrDefault(item =>
        {
            var text = $"{item.LimitName} {item.MeteredFeature}".ToLowerInvariant();
            return text.Contains("spark") || text.Contains("bengalfox");
        });
        var sparkWindows = NormalizeWindows(spark?.RateLimit);

        return new UsageSnapshot(
            credentials.ProfileId,
            credentials.Email,
            string.IsNullOrWhiteSpace(payload.PlanType)
                ? "ChatGPT"
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(payload.PlanType.ToLowerInvariant()),
            mainWindows.Session?.ToModel(),
            mainWindows.Weekly?.ToModel(),
            sparkWindows.Session?.ToModel(),
            sparkWindows.Weekly?.ToModel(),
            payload.Credits?.BalanceValue,
            payload.Credits?.Unlimited ?? false,
            resetCredits?.AvailableCount,
            resetCredits?.ExpirationDates ?? [],
            DateTimeOffset.UtcNow);
    }

    private async Task<ResetCreditsPayload?> ResolveResetCreditsAsync(
        ResetCreditsPayload? embedded,
        ProfileCredentials credentials,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        ResetCreditsRequestState state;
        lock (_resetCreditsGate)
        {
            if (!_resetCreditsStates.TryGetValue(credentials.AccountId, out state!))
            {
                state = new ResetCreditsRequestState();
                _resetCreditsStates[credentials.AccountId] = state;
            }

            if (embedded?.Credits is not null)
            {
                state.RecordSuccess(embedded, now);
            }
            else if (embedded is not null)
            {
                state.RecordSummary(embedded, now);
            }

            if (!state.BeginRequest(now))
            {
                return state.Payload;
            }
        }

        try
        {
            var fetched = await GetAsync<ResetCreditsPayload>(
                "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits",
                credentials,
                cancellationToken,
                new Dictionary<string, string>
                {
                    ["OpenAI-Beta"] = "codex-1",
                    ["originator"] = "Codex Desktop",
                });
            lock (_resetCreditsGate)
            {
                state.RecordSuccess(fetched, DateTimeOffset.UtcNow);
                return state.Payload;
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_resetCreditsGate)
            {
                state.RecordFailure(DateTimeOffset.UtcNow);
                return state.Payload;
            }
        }
    }

    public static UsageSnapshot ParseUsagePayload(
        string json,
        ProfileCredentials credentials,
        DateTimeOffset? updatedAt = null)
    {
        var payload = JsonSerializer.Deserialize<UsagePayload>(json, JsonOptions)
            ?? throw new CodexSyncBarException("사용량 응답이 비어 있습니다.");
        var mainWindows = NormalizeWindows(payload.RateLimit);
        var spark = payload.AdditionalRateLimits?.FirstOrDefault(item =>
        {
            var text = $"{item.LimitName} {item.MeteredFeature}".ToLowerInvariant();
            return text.Contains("spark") || text.Contains("bengalfox");
        });
        var sparkWindows = NormalizeWindows(spark?.RateLimit);

        return new UsageSnapshot(
            credentials.ProfileId,
            credentials.Email,
            string.IsNullOrWhiteSpace(payload.PlanType) ? "ChatGPT" : payload.PlanType,
            mainWindows.Session?.ToModel(),
            mainWindows.Weekly?.ToModel(),
            sparkWindows.Session?.ToModel(),
            sparkWindows.Weekly?.ToModel(),
            payload.Credits?.BalanceValue,
            payload.Credits?.Unlimited ?? false,
            payload.RateLimitResetCredits?.AvailableCount,
            payload.RateLimitResetCredits?.ExpirationDates ?? [],
            updatedAt ?? DateTimeOffset.UtcNow);
    }

    private async Task<T> GetAsync<T>(
        string url,
        ProfileCredentials credentials,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("CodexSyncBar/1.0 (Windows; WinUI 3)");
        if (extraHeaders is not null)
        {
            foreach (var (key, value) in extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AuthenticationRequiredException("인증이 만료되었거나 취소되었습니다. 다시 로그인해 주세요.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CodexSyncBarException($"사용량 서버 응답 HTTP {(int)response.StatusCode}");
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                ?? throw new CodexSyncBarException("사용량 응답이 비어 있습니다.");
        }
        catch (JsonException error)
        {
            throw new CodexSyncBarException($"사용량 응답을 해석하지 못했습니다: {error.Message}");
        }
    }

    private static (WindowPayload? Session, WindowPayload? Weekly) NormalizeWindows(RateLimitPayload? rateLimit)
    {
        if (rateLimit is null)
        {
            return (null, null);
        }

        var windows = new[] { rateLimit.PrimaryWindow, rateLimit.SecondaryWindow }
            .Where(window => window is not null)
            .Cast<WindowPayload>()
            .ToArray();
        if (windows.Length == 0)
        {
            return (null, null);
        }

        if (windows.Length == 1)
        {
            return windows[0].LimitWindowSeconds >= 86_400
                ? (null, windows[0])
                : (windows[0], null);
        }

        if (windows.All(window => window.LimitWindowSeconds.HasValue))
        {
            var sorted = windows.OrderBy(window => window.LimitWindowSeconds).ToArray();
            return (sorted[0], sorted[^1]);
        }

        return (rateLimit.PrimaryWindow, rateLimit.SecondaryWindow);
    }

    private sealed class UsagePayload
    {
        [JsonPropertyName("plan_type")]
        public string? PlanType { get; set; }

        [JsonPropertyName("rate_limit")]
        public RateLimitPayload? RateLimit { get; set; }

        [JsonPropertyName("credits")]
        public CreditsPayload? Credits { get; set; }

        [JsonPropertyName("additional_rate_limits")]
        public List<AdditionalRateLimitPayload>? AdditionalRateLimits { get; set; }

        [JsonPropertyName("rate_limit_reset_credits")]
        public ResetCreditsPayload? RateLimitResetCredits { get; set; }
    }

    private sealed class RateLimitPayload
    {
        [JsonPropertyName("primary_window")]
        public WindowPayload? PrimaryWindow { get; set; }

        [JsonPropertyName("secondary_window")]
        public WindowPayload? SecondaryWindow { get; set; }
    }

    private sealed class AdditionalRateLimitPayload
    {
        [JsonPropertyName("limit_name")]
        public string? LimitName { get; set; }

        [JsonPropertyName("metered_feature")]
        public string? MeteredFeature { get; set; }

        [JsonPropertyName("rate_limit")]
        public RateLimitPayload? RateLimit { get; set; }
    }

    private sealed class WindowPayload
    {
        [JsonPropertyName("used_percent")]
        public JsonElement UsedPercent { get; set; }

        [JsonPropertyName("reset_at")]
        public long? ResetAt { get; set; }

        [JsonPropertyName("limit_window_seconds")]
        public int? LimitWindowSeconds { get; set; }

        public UsageWindow ToModel() => new(
            ReadDouble(UsedPercent),
            ResetAt.HasValue ? DateTimeOffset.FromUnixTimeSeconds(ResetAt.Value) : null,
            LimitWindowSeconds);
    }

    private sealed class CreditsPayload
    {
        [JsonPropertyName("unlimited")]
        public bool? Unlimited { get; set; }

        [JsonPropertyName("balance")]
        public JsonElement Balance { get; set; }

        public double? BalanceValue => Balance.ValueKind switch
        {
            JsonValueKind.Number when Balance.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(
                Balance.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
            _ => null,
        };
    }

    private sealed class ResetCreditsPayload
    {
        [JsonPropertyName("available_count")]
        public int AvailableCount { get; set; }

        [JsonPropertyName("credits")]
        public List<ResetCreditPayload>? Credits { get; set; }

        public IReadOnlyList<DateTimeOffset> ExpirationDates => (Credits ?? [])
            .Select(credit => credit.ExpirationDate)
            .Where(date => date.HasValue)
            .Select(date => date!.Value)
            .OrderBy(date => date)
            .ToArray();
    }

    private sealed class ResetCreditPayload
    {
        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; set; }

        public DateTimeOffset? ExpirationDate => DateTimeOffset.TryParse(
            ExpiresAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var value)
            ? value
            : null;
    }

    private sealed class ResetCreditsRequestState
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(15);

        public ResetCreditsPayload? Payload { get; private set; }

        private DateTimeOffset? NextRequestAt { get; set; }

        public bool BeginRequest(DateTimeOffset now)
        {
            if (NextRequestAt is { } next && next > now)
            {
                return false;
            }

            NextRequestAt = now + FailureRetryInterval;
            return true;
        }

        public void RecordSuccess(ResetCreditsPayload payload, DateTimeOffset now)
        {
            if (payload.Credits is null
                && Payload?.AvailableCount == payload.AvailableCount
                && Payload.ExpirationDates.Any(date => date > now))
            {
                Payload = new ResetCreditsPayload
                {
                    AvailableCount = payload.AvailableCount,
                    Credits = Payload.Credits,
                };
            }
            else
            {
                Payload = payload;
            }

            NextRequestAt = now + RefreshInterval;
        }

        public void RecordSummary(ResetCreditsPayload summary, DateTimeOffset now)
        {
            if (Payload?.AvailableCount == summary.AvailableCount
                && Payload.ExpirationDates.Any(date => date > now))
            {
                Payload = new ResetCreditsPayload
                {
                    AvailableCount = summary.AvailableCount,
                    Credits = Payload.Credits,
                };
            }
            else
            {
                Payload = summary;
            }
        }

        public void RecordFailure(DateTimeOffset now) =>
            NextRequestAt = now + FailureRetryInterval;
    }

    private static double ReadDouble(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value))
        {
            return value;
        }

        if (element.ValueKind == JsonValueKind.String
            && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        throw new CodexSyncBarException("사용량 응답의 used_percent 값이 올바르지 않습니다.");
    }
}
