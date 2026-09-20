using System.Text.Json;

namespace CodexSyncBar.Windows.Core;

public sealed class LogoutTransactionManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Operation { get; set; } = string.Empty;
    public int ProfileId { get; set; }
    public int FallbackProfileId { get; set; }
    public List<string> DeviceIds { get; set; } = [];
    public List<string> FallbackSwitchAttemptedDeviceIds { get; set; } = [];
    public List<string> FallbackSwitchedDeviceIds { get; set; } = [];
    public List<string> StageAttemptedDeviceIds { get; set; } = [];
    public List<string> StagedDeviceIds { get; set; } = [];
    public List<string> CommittedDeviceIds { get; set; } = [];
    public bool LocalFallbackSwitched { get; set; }
    public string State { get; set; } = "staging";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastError { get; set; }
}

public sealed record LogoutRecoveryResult(
    IReadOnlyList<string> PendingOperations,
    IReadOnlyList<int> CompletedProfileIds);

public sealed class LogoutTransactionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WindowsPaths _paths;

    public LogoutTransactionStore(WindowsPaths paths)
    {
        _paths = paths;
    }

    public string DirectoryFor(string operation) =>
        Path.Combine(_paths.LogoutTransactionsDirectory, operation);

    public string BackupFileFor(string operation) =>
        Path.Combine(DirectoryFor(operation), "profile.auth.json");

    public string ManifestFileFor(string operation) =>
        Path.Combine(DirectoryFor(operation), "manifest.json");

    public void Save(LogoutTransactionManifest manifest)
    {
        ValidateManifest(manifest);
        var directory = DirectoryFor(manifest.Operation);
        Directory.CreateDirectory(directory);
        manifest.UpdatedAt = DateTimeOffset.UtcNow;
        var temporary = Path.Combine(directory, $".manifest.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, JsonOptions));
        try
        {
            File.Move(temporary, ManifestFileFor(manifest.Operation), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public LogoutTransactionManifest? Load(string operation)
    {
        var directory = DirectoryFor(operation);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        EnsureSafeDirectory(directory);
        var path = ManifestFileFor(operation);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new CodexSyncBarException($"로그아웃 복구 기록이 안전하지 않습니다: {path}");
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<LogoutTransactionManifest>(
                File.ReadAllText(path),
                JsonOptions)
                ?? throw new CodexSyncBarException("로그아웃 복구 기록이 비어 있습니다.");
            ValidateManifest(manifest);
            if (!string.Equals(manifest.Operation, operation, StringComparison.Ordinal))
            {
                throw new CodexSyncBarException("로그아웃 복구 기록의 작업 ID가 경로와 다릅니다.");
            }

            return manifest;
        }
        catch (JsonException error)
        {
            throw new CodexSyncBarException("로그아웃 복구 기록을 읽지 못했습니다.", error);
        }
    }

    public IReadOnlyList<LogoutTransactionManifest> LoadAll()
    {
        if (!Directory.Exists(_paths.LogoutTransactionsDirectory))
        {
            return [];
        }

        EnsureSafeDirectory(_paths.LogoutTransactionsDirectory);
        var manifests = new List<LogoutTransactionManifest>();
        foreach (var directory in Directory.EnumerateDirectories(_paths.LogoutTransactionsDirectory))
        {
            var operation = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(operation))
            {
                continue;
            }

            manifests.Add(Load(operation)
                ?? throw new CodexSyncBarException($"로그아웃 복구 기록이 없습니다: {directory}"));
        }

        return manifests;
    }

    public void Delete(string operation)
    {
        var directory = DirectoryFor(operation);
        if (!Directory.Exists(directory))
        {
            return;
        }

        EnsureSafeDirectory(directory);
        Directory.Delete(directory, recursive: true);
    }

    private static void ValidateManifest(LogoutTransactionManifest manifest)
    {
        if (manifest.SchemaVersion != LogoutTransactionManifest.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(manifest.Operation)
            || !System.Text.RegularExpressions.Regex.IsMatch(
                manifest.Operation,
                "^[a-zA-Z0-9_-]{1,120}$")
            || manifest.ProfileId <= 0
            || manifest.FallbackProfileId <= 0
            || manifest.ProfileId == manifest.FallbackProfileId
            || manifest.State is not ("staging" or "committing"))
        {
            throw new CodexSyncBarException("로그아웃 복구 기록의 형식이 올바르지 않습니다.");
        }

        if (manifest.DeviceIds.Any(string.IsNullOrWhiteSpace)
            || manifest.DeviceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.DeviceIds.Count
            || manifest.FallbackSwitchAttemptedDeviceIds.Any(id =>
                !manifest.DeviceIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            || manifest.FallbackSwitchAttemptedDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != manifest.FallbackSwitchAttemptedDeviceIds.Count
            || manifest.FallbackSwitchedDeviceIds.Any(id =>
                !manifest.DeviceIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            || manifest.FallbackSwitchedDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != manifest.FallbackSwitchedDeviceIds.Count
            || manifest.StageAttemptedDeviceIds.Any(id =>
                !manifest.DeviceIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            || manifest.StageAttemptedDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != manifest.StageAttemptedDeviceIds.Count
            || manifest.StagedDeviceIds.Any(id => !manifest.DeviceIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            || manifest.CommittedDeviceIds.Any(id => !manifest.DeviceIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
        {
            throw new CodexSyncBarException("로그아웃 복구 기록의 장치 목록이 올바르지 않습니다.");
        }
    }

    private static void EnsureSafeDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new CodexSyncBarException($"로그아웃 복구 디렉터리가 안전하지 않습니다: {path}");
        }
    }
}
