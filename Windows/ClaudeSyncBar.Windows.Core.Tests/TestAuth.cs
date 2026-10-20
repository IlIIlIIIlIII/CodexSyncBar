using System.Text.Json;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Windows.Core.Tests;

/// <summary>Claude login documents for tests; no value resembles a real token.</summary>
internal static class TestAuth
{
    public static ClaudeAuthFile Create(
        string account,
        string access,
        string refresh,
        string email = "test@example.com",
        long? expiresAt = null,
        string? organization = null) => new()
    {
        Oauth = new()
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresAt = expiresAt,
            Scopes = ["user:inference", "user:profile"],
            SubscriptionType = "max",
            RateLimitTier = "default_claude_max_20x",
        },
        Account = new() { AccountUuid = account, EmailAddress = email, OrganizationUuid = organization },
    };

    public static byte[] Bytes(ClaudeAuthFile auth) => JsonSerializer.SerializeToUtf8Bytes(auth);
}
