using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeSyncBar.Windows.Core;

public sealed class UsageService
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string ProfileUrl = "https://api.anthropic.com/api/oauth/profile";
    private const int WeeklySeconds = 604800;
    private const int FiveHourSeconds = 18000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    public UsageService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(25);
    }

    public async Task<UsageSnapshot> FetchAsync(
        ProfileCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var payload = await GetAsync<UsagePayload>(UsageUrl, credentials, cancellationToken);
        return ToSnapshot(payload, credentials, DateTimeOffset.UtcNow);
    }

    /// <summary>The account an access token belongs to, or null when the token is no longer accepted.</summary>
    public async Task<ClaudeOauthAccount?> FetchAccountAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ProfilePayload payload;
        try
        {
            payload = await GetAsync<ProfilePayload>(
                ProfileUrl,
                new ProfileCredentials(0, accessToken, string.Empty, string.Empty, string.Empty, null, string.Empty, false),
                cancellationToken);
        }
        catch (AuthenticationRequiredException)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(payload.Account?.Uuid)
            ? null
            : new ClaudeOauthAccount
            {
                AccountUuid = payload.Account.Uuid,
                EmailAddress = payload.Account.Email,
                OrganizationUuid = payload.Organization?.Uuid,
            };
    }

    public static UsageSnapshot ParseUsagePayload(
        string json,
        ProfileCredentials credentials,
        DateTimeOffset? updatedAt = null)
    {
        var payload = JsonSerializer.Deserialize<UsagePayload>(json, JsonOptions)
            ?? throw new ClaudeSyncBarException("사용량 응답이 비어 있습니다.");
        return ToSnapshot(payload, credentials, updatedAt ?? DateTimeOffset.UtcNow);
    }

    // The service names each quota by its period and sends no duration. The
    // five-hour and seven-day entries are read; model-specific entries are not.
    private static UsageSnapshot ToSnapshot(UsagePayload payload, ProfileCredentials credentials, DateTimeOffset updatedAt) => new(
        credentials.ProfileId,
        credentials.Email,
        string.IsNullOrWhiteSpace(credentials.Plan) ? "Claude" : credentials.Plan,
        ReadWindow(payload.SevenDay, WeeklySeconds),
        updatedAt,
        ReadOptionalWindow(payload.FiveHour, FiveHourSeconds));

    private async Task<T> GetAsync<T>(
        string url,
        ProfileCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("ClaudeSyncBar/1.0 (Ubuntu; GTK 4)");

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
            throw new ClaudeSyncBarException($"사용량 서버 응답 HTTP {(int)response.StatusCode}");
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                ?? throw new ClaudeSyncBarException("사용량 응답이 비어 있습니다.");
        }
        catch (JsonException error)
        {
            throw new ClaudeSyncBarException($"사용량 응답을 해석하지 못했습니다: {error.Message}");
        }
    }

    // A malformed five-hour entry hides only that quota; the weekly one still shows.
    private static UsageWindow? ReadOptionalWindow(JsonElement window, int durationSeconds)
    {
        try { return ReadWindow(window, durationSeconds); }
        catch (ClaudeSyncBarException) { return null; }
    }

    private static UsageWindow? ReadWindow(JsonElement window, int durationSeconds)
    {
        if (window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("utilization", out var utilization)
            || utilization.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var resetsAt = window.TryGetProperty("resets_at", out var reset)
            && reset.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                reset.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : (DateTimeOffset?)null;
        return new UsageWindow(ReadDouble(utilization), resetsAt, durationSeconds);
    }

    private sealed class UsagePayload
    {
        [JsonPropertyName("five_hour")]
        public JsonElement FiveHour { get; set; }

        [JsonPropertyName("seven_day")]
        public JsonElement SevenDay { get; set; }
    }

    private sealed class ProfilePayload
    {
        [JsonPropertyName("account")]
        public ProfileEntry? Account { get; set; }

        [JsonPropertyName("organization")]
        public ProfileEntry? Organization { get; set; }
    }

    private sealed class ProfileEntry
    {
        [JsonPropertyName("uuid")]
        public string? Uuid { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }
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

        throw new ClaudeSyncBarException("사용량 응답의 utilization 값이 올바르지 않습니다.");
    }
}
