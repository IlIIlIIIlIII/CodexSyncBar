using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

public sealed class DeviceActivationTransaction
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Operation { get; set; } = string.Empty;
    public SshDeviceConfiguration OriginalDevice { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Durable intent written immediately before a disabled SSH device is made
/// active. If the process exits after the configuration commit but before
/// remote verification finishes, startup can restore the original disabled
/// configuration instead of leaving a half-activated device enabled.
/// </summary>
public sealed class DeviceActivationTransactionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WindowsPaths _paths;

    public DeviceActivationTransactionStore(WindowsPaths paths)
    {
        _paths = paths;
    }

    public string DirectoryPath => _paths.DeviceActivationTransactionsDirectory;

    public string Save(SshDeviceConfiguration originalDevice)
    {
        if (originalDevice.Enabled)
        {
            throw new CodexSyncBarException("이미 활성화된 SSH 장치는 활성화 트랜잭션을 만들 수 없습니다.");
        }

        var operation = $"activation_{Guid.NewGuid():N}";
        var transaction = new DeviceActivationTransaction
        {
            Operation = operation,
            OriginalDevice = DeviceConfigurationComparer.Clone(originalDevice),
        };
        WindowsPathSafety.EnsureDirectory(DirectoryPath, "SSH 장치 활성화 복구 디렉터리");
        var destination = Path.Combine(DirectoryPath, operation + ".json");
        WindowsPathSafety.EnsureFile(destination, "SSH 장치 활성화 복구 기록");
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

    public void Recover(AppConfiguration configuration, ConfigurationStore configurationStore)
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return;
        }

        WindowsPathSafety.EnsureDirectory(DirectoryPath, "SSH 장치 활성화 복구 디렉터리");
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            WindowsPathSafety.EnsureFile(path, "SSH 장치 활성화 복구 기록");
            var transaction = Read(path);
            configurationStore.RollbackDeviceActivation(configuration, transaction.OriginalDevice);
            File.Delete(path);
        }
    }

    public void Delete(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetFullPath(DirectoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
        {
            throw new CodexSyncBarException("SSH 장치 활성화 복구 기록 경로가 올바르지 않습니다.");
        }

        WindowsPathSafety.EnsureFile(fullPath, "SSH 장치 활성화 복구 기록");
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    private static DeviceActivationTransaction Read(string path)
    {
        try
        {
            var transaction = JsonSerializer.Deserialize<DeviceActivationTransaction>(
                                  File.ReadAllText(path),
                                  JsonOptions)
                ?? throw new CodexSyncBarException("SSH 장치 활성화 복구 기록이 비어 있습니다.");
            if (transaction.SchemaVersion != DeviceActivationTransaction.CurrentSchemaVersion
                || !Regex.IsMatch(transaction.Operation, "^activation_[a-f0-9]{32}$")
                || transaction.OriginalDevice.Enabled)
            {
                throw new CodexSyncBarException("SSH 장치 활성화 복구 기록의 형식이 올바르지 않습니다.");
            }

            return transaction;
        }
        catch (JsonException error)
        {
            throw new CodexSyncBarException("SSH 장치 활성화 복구 기록을 읽지 못했습니다.", error);
        }
    }
}
