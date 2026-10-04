using System.Text.Json;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Ubuntu.Backend;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    public static string Text(JsonElement p, string key, string fallback = "") => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
    public static int Number(JsonElement p, string key, int fallback = 0) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : fallback;
    public static bool Flag(JsonElement p, string key, bool fallback = false) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
    public static JsonElement Item(JsonElement p, string key) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) ? v : default;
}

public sealed class RpcException(string code, string message, object? data = null) : Exception(message)
{
    public string Code { get; } = code;
    public object? DataValue { get; } = data;
}
public sealed record RpcRequest(string Id, string Method, JsonElement Params);
public sealed record RpcError(string Code, string Message, object? Data = null);
public sealed record RpcResponse(string? Id, bool Ok, object? Result = null, RpcError? Error = null);
public sealed record AccountView(int Id, string Email, string Alias, bool IsPending, bool NeedsLogin, DashboardUsage? Usage);
public sealed record AccountIdentityHash(int ProfileId, string AccountIdSha256, string EmailSha256);
public sealed record AccountIdentityHashes(IReadOnlyList<AccountIdentityHash> Profiles, string ConfigurationRevision, string CodexHomeSha256);
public sealed record DeviceView(string Id, string DisplayName, string Kind, bool Enabled, bool IsReachable,
    int? CurrentProfileId, string CurrentAccountLabel, DateTimeOffset? ObservedAt, string Status,
    string? Detail = null, bool ReconnectionRequired = false);
public sealed record PreviewTarget(DeviceView Device, int TargetProfileId, string TargetAccountLabel, string Action, bool Included)
{
    public string Id => Device.Id;
    public string DisplayName => Device.DisplayName;
    public string Kind => Device.Kind;
    public bool Enabled => Device.Enabled;
    public bool IsReachable => Device.IsReachable;
    public int? CurrentProfileId => Device.CurrentProfileId;
    public string CurrentAccountLabel => Device.CurrentAccountLabel;
    public DateTimeOffset? ObservedAt => Device.ObservedAt;
    public string Status => Device.Status;
    public string? Detail => Device.Detail;
    public bool ReconnectionRequired => Device.ReconnectionRequired;
}
public sealed record SwitchPreview(string PreviewId, int ProfileId, string ConfigurationRevision,
    DateTimeOffset CreatedAt, bool CanApply, string? BlockingReason, IReadOnlyList<PreviewTarget> Targets);
public sealed record OperationView(string Id, string Kind, int? ProfileId, string State, string Message,
    IReadOnlyList<SwitchTargetResult> Targets, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt = null,
    object? Result = null, string? ErrorCode = null)
{
    public bool IsComplete => State is "completed" or "failed" or "recoveryRequired" or "cancelled";
}
public sealed record ManagementSnapshot(int SchemaVersion, IReadOnlyList<AccountView> Accounts,
    IReadOnlyList<DeviceView> Devices, int? ActiveProfileId, bool IsBusy, OperationView? Operation,
    string ConfigurationRevision, DateTimeOffset UpdatedAt, string? Error, object Settings,
    object? Usage = null, bool IsDemo = false);
public interface IManagementService : IDisposable
{
    bool ShutdownRequested { get; }
    Task<object?> DispatchAsync(string method, JsonElement parameters, CancellationToken token);
    Task StopAsync();
}
