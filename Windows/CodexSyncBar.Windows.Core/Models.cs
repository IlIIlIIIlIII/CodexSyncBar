using System.Text.Json.Serialization;

namespace CodexSyncBar.Windows.Core;

public sealed class AppConfiguration
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("nextAccountID")]
    public int NextAccountId { get; set; } = 1;

    [JsonPropertyName("accounts")]
    public List<AccountProfile> Accounts { get; set; } = [];

    [JsonPropertyName("devices")]
    public List<SshDeviceConfiguration> Devices { get; set; } = [];
}

public sealed class AccountProfile
{
    public const int MaximumAliasLength = 5;

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("alias")]
    public string? CustomAlias { get; set; }

    [JsonPropertyName("isPending")]
    public bool IsPending { get; set; }

    [JsonPropertyName("needsLogin")]
    public bool NeedsLogin { get; set; }

    [JsonIgnore]
    public string Alias => string.IsNullOrWhiteSpace(CustomAlias) ? Email : CustomAlias!;

    [JsonIgnore]
    public string ShortName => !string.IsNullOrWhiteSpace(CustomAlias)
        ? CustomAlias!
        : string.IsNullOrWhiteSpace(Email)
            ? Id.ToString()
            : Email[..1].ToUpperInvariant();

    [JsonIgnore]
    public string StateLabel => IsPending
        ? "로그인 필요"
        : NeedsLogin
            ? "로그아웃됨 · 재로그인 필요"
            : "";

    public override string ToString() => Alias;
}

public sealed class SshDeviceConfiguration
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("credentialID")]
    public Guid? CredentialId { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 22;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("authentication")]
    public string Authentication { get; set; } = "openSSHConfig";

    [JsonPropertyName("identityFile")]
    public string? IdentityFile { get; set; }

    [JsonPropertyName("certificateFile")]
    public string? CertificateFile { get; set; }

    [JsonPropertyName("hasPassword")]
    public bool HasPassword { get; set; }

    [JsonPropertyName("hasKeyPassphrase")]
    public bool HasKeyPassphrase { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName) ? Host : DisplayName;
}

public sealed record UsageWindow(
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    int? DurationSeconds)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

public sealed record UsageSnapshot(
    int ProfileId,
    string Email,
    string Plan,
    UsageWindow? Session,
    UsageWindow? Weekly,
    UsageWindow? SparkSession,
    UsageWindow? SparkWeekly,
    double? CreditBalance,
    bool UnlimitedCredits,
    int? ResetCredits,
    IReadOnlyList<DateTimeOffset> ResetCreditExpirations,
    DateTimeOffset UpdatedAt)
{
    public int? MenuRemainingPercent => Weekly is null
        ? null
        : (int)Math.Round(Weekly.RemainingPercent, MidpointRounding.AwayFromZero);
}

public sealed record DeviceStatus(
    string Id,
    string DisplayName,
    int? ProfileId,
    string? AccountId,
    string? AuthMode,
    string? CliState,
    bool IsReachable,
    string? Detail = null);

public sealed record ProfileCredentials(
    int ProfileId,
    string AccessToken,
    string? IdToken,
    string RefreshToken,
    string AccountId,
    string Email,
    DateTimeOffset? ExpiresAt,
    string SourcePath,
    bool IsActive);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => string.Join(
        Environment.NewLine,
        new[] { StandardOutput, StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record SshTestResult(string DeviceId, bool IsReachable, string Message);

public class CodexSyncBarException : Exception
{
    public CodexSyncBarException(string message)
        : base(message)
    {
    }

    public CodexSyncBarException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class AuthenticationRequiredException(string message) : CodexSyncBarException(message);
