using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

public sealed class SecretCleanupTransaction
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Operation { get; set; } = string.Empty;
    public Guid CredentialId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SecretCleanupTransactionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WindowsPaths _paths;

    public SecretCleanupTransactionStore(WindowsPaths paths)
    {
        _paths = paths;
    }

    public string DirectoryPath => _paths.SecretCleanupTransactionsDirectory;

    public string Begin(Guid credentialId)
    {
        var operation = $"secret_{Guid.NewGuid():N}";
        var transaction = new SecretCleanupTransaction
        {
            Operation = operation,
            CredentialId = credentialId,
        };
        WindowsPathSafety.EnsureDirectory(DirectoryPath, "SSH 비밀 삭제 복구 디렉터리");
        var destination = Path.Combine(DirectoryPath, operation + ".json");
        WindowsPathSafety.EnsureFile(destination, "SSH 비밀 삭제 복구 기록");
        var temporary = Path.Combine(DirectoryPath, $".{operation}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(transaction, JsonOptions));
        try
        {
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return destination;
    }

    public IReadOnlyList<SecretCleanupTransaction> LoadAll()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }

        WindowsPathSafety.EnsureDirectory(DirectoryPath, "SSH 비밀 삭제 복구 디렉터리");
        return Directory.EnumerateFiles(DirectoryPath, "*.json")
            .Select(Read)
            .ToArray();
    }

    public void Delete(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetFullPath(DirectoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
        {
            throw new CodexSyncBarException("SSH 비밀 삭제 복구 기록 경로가 올바르지 않습니다.");
        }

        WindowsPathSafety.EnsureFile(fullPath, "SSH 비밀 삭제 복구 기록");
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    private static SecretCleanupTransaction Read(string path)
    {
        WindowsPathSafety.EnsureFile(path, "SSH 비밀 삭제 복구 기록");
        try
        {
            var transaction = JsonSerializer.Deserialize<SecretCleanupTransaction>(
                                  File.ReadAllText(path),
                                  JsonOptions)
                ?? throw new CodexSyncBarException("SSH 비밀 삭제 복구 기록이 비어 있습니다.");
            if (transaction.SchemaVersion != SecretCleanupTransaction.CurrentSchemaVersion
                || !Regex.IsMatch(transaction.Operation, "^secret_[a-f0-9]{32}$")
                || transaction.CredentialId == Guid.Empty)
            {
                throw new CodexSyncBarException("SSH 비밀 삭제 복구 기록의 형식이 올바르지 않습니다.");
            }

            return transaction;
        }
        catch (JsonException error)
        {
            throw new CodexSyncBarException("SSH 비밀 삭제 복구 기록을 읽지 못했습니다.", error);
        }
    }
}
