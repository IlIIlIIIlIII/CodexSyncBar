using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSyncBar.Windows.Core;

public sealed record DashboardPipeRequest(
    string Method, string RequestId, int? ProfileId = null,
    string? ConfigurationRevision = null, string? OperationId = null);

public sealed record DashboardPipeResponse(
    bool Success, DashboardSnapshot? Snapshot = null, SwitchOperation? Operation = null,
    string? Error = null, string? ErrorCode = null);

public sealed class DashboardPipeException(string code, string message) : CodexSyncBarException(message)
{
    public string Code { get; } = code;
}

public static class DashboardPipeProtocol
{
    public const int MaximumFrameBytes = 256 * 1024;
    public const int MaximumRequestBytes = 4096;
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24,
    };

    public static bool IsValid(DashboardPipeRequest request) =>
        Guid.TryParseExact(request.RequestId, "N", out _) && request.Method switch
        {
            "GetSnapshot" or "RefreshUsage" or "OpenSettings" =>
                request.ProfileId is null && request.ConfigurationRevision is null && request.OperationId is null,
            "SwitchAccount" => request.ProfileId > 0 &&
                request.ConfigurationRevision is { Length: > 0 and <= 128 } && request.OperationId is null,
            "GetOperationStatus" => request.OperationId is { Length: > 0 and <= 128 } &&
                request.ProfileId is null && request.ConfigurationRevision is null,
            _ => false,
        };

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length > MaximumFrameBytes)
            throw new InvalidDataException("위젯 응답이 허용 크기를 초과했습니다.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 2 || length > maximumBytes)
            throw new InvalidDataException("위젯 메시지 크기가 올바르지 않습니다.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
            ?? throw new InvalidDataException("빈 위젯 메시지입니다.");
    }
}
