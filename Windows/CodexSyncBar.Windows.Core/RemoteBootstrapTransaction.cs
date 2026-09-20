using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

internal sealed class RemoteBootstrapTransaction
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("operation")]
    public required string Operation { get; init; }

    [JsonPropertyName("deviceID")]
    public required string DeviceId { get; init; }

    [JsonPropertyName("endpointSHA256")]
    public required string EndpointSha256 { get; init; }

    [JsonPropertyName("archiveFileName")]
    public required string ArchiveFileName { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed class RemoteBootstrapTransactionStore
{
    private const long MaximumArchiveBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private static readonly Regex OperationPattern = new(
        "^bootstrap_[a-f0-9]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DevicePattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly WindowsPaths _paths;

    public RemoteBootstrapTransactionStore(WindowsPaths paths)
    {
        _paths = paths;
    }

    public string DirectoryPath => _paths.RemoteBootstrapTransactionsDirectory;

    public RemoteBootstrapTransaction Begin(
        string deviceId,
        string endpointSha256,
        byte[] archive)
    {
        if (!DevicePattern.IsMatch(deviceId)
            || !Regex.IsMatch(endpointSha256, "^[a-f0-9]{64}$")
            || archive.LongLength > MaximumArchiveBytes)
        {
            throw new CodexSyncBarException("원격 부트스트랩 복구 기록의 입력이 올바르지 않습니다.");
        }

        var pending = LoadAll();
        if (pending.Any(item => string.Equals(item.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CodexSyncBarException(
                $"{deviceId} 장치의 이전 부트스트랩 복구가 끝나지 않았습니다. 먼저 복구를 완료해 주세요.");
        }

        EnsureDirectory();
        var operation = $"bootstrap_{Guid.NewGuid():N}";
        var transaction = new RemoteBootstrapTransaction
        {
            Operation = operation,
            DeviceId = deviceId,
            EndpointSha256 = endpointSha256,
            ArchiveFileName = operation + ".tar",
        };
        var archivePath = ArchivePath(transaction);
        var manifestPath = ManifestPath(transaction);
        try
        {
            AtomicWrite(archivePath, archive);
            WindowsPathSafety.EnsurePrivateFile(
                archivePath,
                "원격 부트스트랩 복구 archive",
                MaximumArchiveBytes);
            AtomicWrite(
                manifestPath,
                JsonSerializer.SerializeToUtf8Bytes(transaction, JsonOptions));
            WindowsPathSafety.EnsurePrivateFile(
                manifestPath,
                "원격 부트스트랩 복구 기록",
                64 * 1024);
            return transaction;
        }
        catch
        {
            TryDelete(archivePath);
            TryDelete(manifestPath);
            throw;
        }
    }

    public IReadOnlyList<RemoteBootstrapTransaction> LoadAll()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }

        EnsureDirectory();
        return Directory.EnumerateFiles(DirectoryPath, "*.json")
            .Select(Read)
            .OrderBy(item => item.CreatedAt)
            .ToArray();
    }

    public string ArchivePath(RemoteBootstrapTransaction transaction)
    {
        Validate(transaction);
        return Path.Combine(DirectoryPath, transaction.ArchiveFileName);
    }

    public string ManifestPath(RemoteBootstrapTransaction transaction) =>
        Path.Combine(DirectoryPath, transaction.Operation + ".json");

    public byte[] ReadArchive(RemoteBootstrapTransaction transaction)
    {
        var path = ArchivePath(transaction);
        return WindowsPathSafety.ReadPrivateFile(
            path,
            "원격 부트스트랩 복구 archive",
            MaximumArchiveBytes);
    }

    public void Delete(RemoteBootstrapTransaction transaction)
    {
        Validate(transaction);
        var directory = Path.GetFullPath(DirectoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        // Remove the manifest first. If deletion is interrupted, a leftover
        // archive is harmless; a leftover manifest without its archive would
        // make deterministic recovery impossible.
        foreach (var path in new[] { ManifestPath(transaction), ArchivePath(transaction) })
        {
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            {
                throw new CodexSyncBarException("원격 부트스트랩 복구 기록 경로가 올바르지 않습니다.");
            }

            WindowsPathSafety.EnsureFile(fullPath, "원격 부트스트랩 복구 기록");
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
    }

    private RemoteBootstrapTransaction Read(string path)
    {
        WindowsPathSafety.EnsurePrivateFile(path, "원격 부트스트랩 복구 기록", 64 * 1024);
        try
        {
            var transaction = JsonSerializer.Deserialize<RemoteBootstrapTransaction>(
                                  File.ReadAllBytes(path),
                                  JsonOptions)
                ?? throw new CodexSyncBarException("원격 부트스트랩 복구 기록이 비어 있습니다.");
            Validate(transaction);
            if (!string.Equals(
                    Path.GetFileName(path),
                    transaction.Operation + ".json",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new CodexSyncBarException("원격 부트스트랩 복구 기록의 작업 ID가 경로와 다릅니다.");
            }

            var archivePath = ArchivePath(transaction);
            WindowsPathSafety.EnsurePrivateFile(
                archivePath,
                "원격 부트스트랩 복구 archive",
                MaximumArchiveBytes);
            if (!File.Exists(archivePath))
            {
                throw new CodexSyncBarException("원격 부트스트랩 복구 archive가 없습니다.");
            }

            return transaction;
        }
        catch (JsonException error)
        {
            throw new CodexSyncBarException("원격 부트스트랩 복구 기록을 읽지 못했습니다.", error);
        }
    }

    private void EnsureDirectory() =>
        WindowsPathSafety.EnsureDirectory(DirectoryPath, "원격 부트스트랩 복구 디렉터리");

    private static void Validate(RemoteBootstrapTransaction transaction)
    {
        if (transaction.SchemaVersion != RemoteBootstrapTransaction.CurrentSchemaVersion
            || !OperationPattern.IsMatch(transaction.Operation)
            || !DevicePattern.IsMatch(transaction.DeviceId)
            || !Regex.IsMatch(transaction.EndpointSha256, "^[a-f0-9]{64}$")
            || !string.Equals(
                transaction.ArchiveFileName,
                transaction.Operation + ".tar",
                StringComparison.Ordinal))
        {
            throw new CodexSyncBarException("원격 부트스트랩 복구 기록의 형식이 올바르지 않습니다.");
        }
    }

    private static void AtomicWrite(string path, byte[] contents)
    {
        WindowsPathSafety.EnsureFile(path, "원격 부트스트랩 복구 파일");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temporary, contents);
        try
        {
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
