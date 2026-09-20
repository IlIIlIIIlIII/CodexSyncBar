using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

public sealed class LoginTransactionManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Operation { get; set; } = string.Empty;
    public int ProfileId { get; set; }
    public bool ProfileExisted { get; set; }
    public bool ActiveReplaced { get; set; }
    public string IncomingAccountId { get; set; } = string.Empty;
    public string State { get; set; } = "collecting";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Provides the same crash boundary as macOS gpt-switch import-login. The
/// incoming full auth, the previous profile, and (when replacing the active
/// account) the active auth are staged before either canonical file changes.
/// </summary>
public sealed class LoginTransactionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WindowsPaths _paths;

    public LoginTransactionStore(WindowsPaths paths)
    {
        _paths = paths;
    }

    public string DirectoryPath => _paths.LoginTransactionsDirectory;

    public string DirectoryFor(string operation) =>
        Path.Combine(DirectoryPath, operation);

    public string ManifestFileFor(string operation) =>
        Path.Combine(DirectoryFor(operation), "manifest.json");

    public string IncomingFileFor(string operation) =>
        Path.Combine(DirectoryFor(operation), "incoming.auth.json");

    public string ProfileBackupFileFor(string operation) =>
        Path.Combine(DirectoryFor(operation), "profile.auth.json");

    public string ActiveBackupFileFor(string operation) =>
        Path.Combine(DirectoryFor(operation), "active.auth.json");

    /// <summary>
    /// Imports a validated full auth file and restores the previous local
    /// state if any part of the canonical install or verification fails.
    /// </summary>
    public void ImportAuth(
        AuthStore authStore,
        string sourcePath,
        int profileId,
        bool replaceExisting = false)
    {
        var manifest = Begin(authStore, sourcePath, profileId);
        try
        {
            authStore.ImportAuth(
                IncomingFileFor(manifest.Operation),
                profileId,
                replaceExisting);

            if (manifest.ActiveReplaced)
            {
                authStore.SwitchActive(profileId);
            }

            VerifyInstalled(authStore, manifest);

            manifest.State = "committed";
            Save(manifest);
            TryDelete(manifest.Operation);
        }
        catch (Exception error)
        {
            try
            {
                RestorePrepared(authStore, manifest);
                TryDelete(manifest.Operation);
            }
            catch (Exception recoveryError)
            {
                throw new CodexSyncBarException(
                    $"로그인 인증 반영에 실패했고 복구 기록을 보존했습니다: {DirectoryFor(manifest.Operation)}",
                    new AggregateException(error, recoveryError));
            }

            throw;
        }
    }

    /// <summary>
    /// Resolves transactions left by an interrupted login/import operation.
    /// A prepared transaction is rolled back; a committed transaction only
    /// needs its private staging directory removed.
    /// </summary>
    public void Recover(AuthStore authStore)
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return;
        }

        WindowsPathSafety.EnsureDirectory(DirectoryPath, "로그인 복구 디렉터리");
        foreach (var directory in Directory.EnumerateDirectories(DirectoryPath))
        {
            var operation = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(operation))
            {
                continue;
            }

            var manifest = Load(operation);
            switch (manifest.State)
            {
                case "collecting":
                    // Canonical files are not touched until the transaction
                    // reaches prepared, so an incomplete staging directory is
                    // safe to discard.
                    Delete(operation);
                    break;
                case "prepared":
                    RestorePrepared(authStore, manifest);
                    Delete(operation);
                    break;
                case "committed":
                    Delete(operation);
                    break;
                default:
                    throw new CodexSyncBarException("로그인 복구 기록의 상태를 해석하지 못했습니다.");
            }
        }
    }

    public LoginTransactionManifest Load(string operation)
    {
        ValidateOperation(operation);
        var directory = DirectoryFor(operation);
        WindowsPathSafety.EnsureDirectory(directory, "로그인 복구 작업 디렉터리");
        var path = ManifestFileFor(operation);
        WindowsPathSafety.EnsureFile(path, "로그인 복구 기록");
        if (!File.Exists(path))
        {
            throw new CodexSyncBarException($"로그인 복구 기록이 없습니다: {path}");
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<LoginTransactionManifest>(
                               File.ReadAllText(path),
                               JsonOptions)
                ?? throw new CodexSyncBarException("로그인 복구 기록이 비어 있습니다.");
            ValidateManifest(manifest);
            if (!string.Equals(manifest.Operation, operation, StringComparison.Ordinal))
            {
                throw new CodexSyncBarException("로그인 복구 기록의 작업 ID가 경로와 다릅니다.");
            }

            return manifest;
        }
        catch (JsonException error)
        {
            throw new CodexSyncBarException("로그인 복구 기록을 읽지 못했습니다.", error);
        }
    }

    public void Delete(string operation)
    {
        ValidateOperation(operation);
        var directory = DirectoryFor(operation);
        if (!Directory.Exists(directory))
        {
            return;
        }

        WindowsPathSafety.EnsureDirectory(directory, "로그인 복구 작업 디렉터리");
        Directory.Delete(directory, recursive: true);
    }

    private LoginTransactionManifest Begin(
        AuthStore authStore,
        string sourcePath,
        int profileId)
    {
        if (profileId <= 0)
        {
            throw new CodexSyncBarException("로그인 프로필 ID가 올바르지 않습니다.");
        }

        var incoming = authStore.ReadAuthFile(sourcePath);
        var destination = authStore.ProfileAuthFile(profileId);
        var profileExisted = File.Exists(destination);
        CodexAuthFile? previous = null;
        if (profileExisted)
        {
            previous = authStore.ReadAuthFile(destination);
        }

        var activeReplaced = profileExisted
            && previous is not null
            && string.Equals(
                authStore.ReadActiveAccountId(),
                previous.Tokens.AccountId,
                StringComparison.Ordinal);

        _paths.EnsureDirectories();
        WindowsPathSafety.EnsureDirectory(DirectoryPath, "로그인 복구 디렉터리");
        var operation = $"login_{Guid.NewGuid():N}";
        var directory = DirectoryFor(operation);
        WindowsPathSafety.EnsureDirectory(directory, "로그인 복구 작업 디렉터리");
        var manifest = new LoginTransactionManifest
        {
            Operation = operation,
            ProfileId = profileId,
            ProfileExisted = profileExisted,
            ActiveReplaced = activeReplaced,
            IncomingAccountId = incoming.Tokens.AccountId!,
        };

        try
        {
            Save(manifest);
            authStore.CopyAuthFile(sourcePath, IncomingFileFor(operation));
            if (profileExisted)
            {
                authStore.CopyAuthFile(destination, ProfileBackupFileFor(operation));
            }

            if (activeReplaced)
            {
                authStore.CopyAuthFile(
                    _paths.ActiveAuthFile,
                    ActiveBackupFileFor(operation));
            }

            manifest.State = "prepared";
            Save(manifest);
            return manifest;
        }
        catch
        {
            TryDelete(operation);
            throw;
        }
    }

    private void Save(LoginTransactionManifest manifest)
    {
        ValidateManifest(manifest);
        var directory = DirectoryFor(manifest.Operation);
        WindowsPathSafety.EnsureDirectory(directory, "로그인 복구 작업 디렉터리");
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

    private void RestorePrepared(AuthStore authStore, LoginTransactionManifest manifest)
    {
        var incoming = authStore.ReadAuthFile(IncomingFileFor(manifest.Operation));
        if (!string.Equals(
                incoming.Tokens.AccountId,
                manifest.IncomingAccountId,
                StringComparison.Ordinal))
        {
            throw new CodexSyncBarException("로그인 복구용 incoming 인증의 계정이 기록과 다릅니다.");
        }

        var destination = authStore.ProfileAuthFile(manifest.ProfileId);
        if (manifest.ProfileExisted)
        {
            EnsureCurrentIsIncomingOrPrevious(authStore, destination, incoming, ProfileBackupFileFor(manifest.Operation));
            authStore.CopyAuthFile(ProfileBackupFileFor(manifest.Operation), destination);
        }
        else
        {
            if (File.Exists(destination))
            {
                var current = authStore.ReadAuthFile(destination);
                if (!string.Equals(
                        current.Tokens.AccountId,
                        incoming.Tokens.AccountId,
                        StringComparison.Ordinal))
                {
                    throw new CodexSyncBarException("로그인 복구 대상 프로필이 외부에서 변경되었습니다.");
                }

                authStore.DeleteProfile(manifest.ProfileId);
            }
        }

        if (manifest.ActiveReplaced)
        {
            EnsureCurrentIsIncomingOrPrevious(
                authStore,
                _paths.ActiveAuthFile,
                incoming,
                ActiveBackupFileFor(manifest.Operation));
            authStore.CopyAuthFile(
                ActiveBackupFileFor(manifest.Operation),
                _paths.ActiveAuthFile);
        }
    }

    private static void EnsureCurrentIsIncomingOrPrevious(
        AuthStore authStore,
        string currentPath,
        CodexAuthFile incoming,
        string previousPath)
    {
        if (!File.Exists(currentPath))
        {
            throw new CodexSyncBarException($"로그인 복구 대상 인증 파일이 사라졌습니다: {currentPath}");
        }

        var current = authStore.ReadAuthFile(currentPath);
        var previous = authStore.ReadAuthFile(previousPath);
        var currentIsIncoming = string.Equals(
            current.Tokens.AccountId,
            incoming.Tokens.AccountId,
            StringComparison.Ordinal);
        var currentIsPrevious = string.Equals(
            current.Tokens.AccountId,
            previous.Tokens.AccountId,
            StringComparison.Ordinal);
        if (!currentIsIncoming && !currentIsPrevious)
        {
            throw new CodexSyncBarException("로그인 복구 대상 인증 파일이 외부에서 변경되었습니다.");
        }
    }

    private void VerifyInstalled(AuthStore authStore, LoginTransactionManifest manifest)
    {
        var incoming = authStore.ReadAuthFile(IncomingFileFor(manifest.Operation));
        var installed = authStore.ReadAuthFile(authStore.ProfileAuthFile(manifest.ProfileId));
        if (!SameTokens(incoming, installed))
        {
            throw new CodexSyncBarException("설치된 프로필 인증을 검증하지 못했습니다.");
        }

        if (manifest.ActiveReplaced)
        {
            var active = authStore.ReadActiveAuth()
                ?? throw new CodexSyncBarException("활성 인증 파일을 검증하지 못했습니다.");
            if (!SameTokens(incoming, active))
            {
                throw new CodexSyncBarException("설치된 활성 인증을 검증하지 못했습니다.");
            }
        }
    }

    private static bool SameTokens(CodexAuthFile first, CodexAuthFile second) =>
        string.Equals(first.AuthMode, second.AuthMode, StringComparison.Ordinal)
        && string.Equals(first.Tokens.AccountId, second.Tokens.AccountId, StringComparison.Ordinal)
        && string.Equals(first.Tokens.AccessToken, second.Tokens.AccessToken, StringComparison.Ordinal)
        && string.Equals(first.Tokens.IdToken, second.Tokens.IdToken, StringComparison.Ordinal)
        && string.Equals(first.Tokens.RefreshToken, second.Tokens.RefreshToken, StringComparison.Ordinal);

    private void TryDelete(string operation)
    {
        try
        {
            Delete(operation);
        }
        catch
        {
            // A committed transaction is safe to clean on the next startup;
            // cleanup failure must not turn a successful auth import into a
            // false negative.
        }
    }

    private static void ValidateOperation(string operation)
    {
        if (!Regex.IsMatch(operation, "^login_[a-f0-9]{32}$"))
        {
            throw new CodexSyncBarException("로그인 복구 작업 ID가 올바르지 않습니다.");
        }
    }

    private static void ValidateManifest(LoginTransactionManifest manifest)
    {
        if (manifest.SchemaVersion != LoginTransactionManifest.CurrentSchemaVersion
            || !Regex.IsMatch(manifest.Operation, "^login_[a-f0-9]{32}$")
            || manifest.ProfileId <= 0
            || manifest.State is not ("collecting" or "prepared" or "committed")
            || (manifest.State is "prepared" or "committed")
                && string.IsNullOrWhiteSpace(manifest.IncomingAccountId)
            || manifest.ActiveReplaced && !manifest.ProfileExisted)
        {
            throw new CodexSyncBarException("로그인 복구 기록의 형식이 올바르지 않습니다.");
        }
    }
}
