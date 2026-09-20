using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

public sealed class ConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly WindowsPaths _paths;

    public ConfigurationStore(WindowsPaths? paths = null)
    {
        _paths = paths ?? new WindowsPaths();
    }

    public WindowsPaths Paths => _paths;

    public AppConfiguration LoadOrCreate()
    {
        lock (_gate)
        {
            _paths.EnsureDirectories();
            WindowsPathSafety.EnsureFile(_paths.ConfigurationFile, "설정 파일");
            if (!File.Exists(_paths.ConfigurationFile))
            {
                var configuration = new AppConfiguration
                {
                    NextAccountId = 2,
                    Accounts =
                    [
                        new AccountProfile
                        {
                            Id = 1,
                            Email = "로그인 전 계정 1",
                            IsPending = true,
                        },
                    ],
                };
                SaveUnlocked(configuration);
                return configuration;
            }

            try
            {
                var json = File.ReadAllText(_paths.ConfigurationFile);
                var configuration = JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions)
                    ?? throw new CodexSyncBarException("설정 파일이 비어 있습니다.");
                var migrated = BackfillCredentialIds(configuration);
                Validate(configuration, checkCredentialFiles: false);
                if (migrated)
                {
                    SaveUnlocked(configuration);
                }
                return configuration;
            }
            catch (JsonException error)
            {
                throw new CodexSyncBarException($"설정 파일을 읽지 못했습니다: {error.Message}");
            }
        }
    }

    public void Save(AppConfiguration configuration)
    {
        lock (_gate)
        {
            Validate(configuration, checkCredentialFiles: false);
            _paths.EnsureDirectories();
            WindowsPathSafety.EnsureFile(_paths.ConfigurationFile, "설정 파일");
            SaveUnlocked(configuration);
        }
    }

    public AccountProfile ReserveAccount(AppConfiguration configuration)
    {
        lock (_gate)
        {
            var id = configuration.NextAccountId;
            if (id <= 0)
            {
                throw new CodexSyncBarException("새 계정 ID를 만들 수 없습니다.");
            }

            var account = new AccountProfile
            {
                Id = id,
                Email = $"로그인 전 계정 {id}",
                IsPending = true,
            };
            configuration.Accounts.Add(account);
            configuration.NextAccountId = id + 1;
            Save(configuration);
            return account;
        }
    }

    public void UpdateAccountEmail(AppConfiguration configuration, int profileId, string email)
    {
        var normalized = email.Trim();
        if (!normalized.Contains('@') || normalized.Any(char.IsWhiteSpace))
        {
            throw new CodexSyncBarException("로그인한 계정 이메일을 확인하지 못했습니다.");
        }

        var account = FindAccount(configuration, profileId);
        account.Email = normalized;
        account.IsPending = false;
        account.NeedsLogin = false;
        Save(configuration);
    }

    public void MarkAccountLoggedOut(AppConfiguration configuration, int profileId)
    {
        var account = FindAccount(configuration, profileId);
        account.IsPending = false;
        account.NeedsLogin = true;
        Save(configuration);
    }

    public void MarkAccountNeedsLogin(AppConfiguration configuration, int profileId)
    {
        var account = FindAccount(configuration, profileId);
        account.IsPending = false;
        account.NeedsLogin = true;
        Save(configuration);
    }

    public void UpdateAccountAlias(AppConfiguration configuration, int profileId, string? alias)
    {
        var normalized = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        if (normalized is not null && (normalized.Length > AccountProfile.MaximumAliasLength
            || normalized.Any(char.IsControl)))
        {
            throw new CodexSyncBarException("계정 별칭은 제어문자 없이 5글자 이하로 입력해 주세요.");
        }

        FindAccount(configuration, profileId).CustomAlias = normalized;
        Save(configuration);
    }

    public void RemoveAccount(AppConfiguration configuration, int profileId)
    {
        if (configuration.Accounts.Count <= 1)
        {
            throw new CodexSyncBarException("마지막 계정은 제거할 수 없습니다.");
        }

        var account = FindAccount(configuration, profileId);
        configuration.Accounts.Remove(account);
        Save(configuration);
    }

    public void ReorderAccounts(AppConfiguration configuration, IReadOnlyList<int> orderedIds)
    {
        lock (_gate)
        {
            var currentIds = configuration.Accounts.Select(account => account.Id).ToHashSet();
            if (orderedIds.Count != currentIds.Count || orderedIds.ToHashSet().SetEquals(currentIds) is false)
            {
                throw new CodexSyncBarException("계정 순서가 현재 계정 목록과 일치하지 않습니다.");
            }

            var byId = configuration.Accounts.ToDictionary(account => account.Id);
            configuration.Accounts = orderedIds.Select(id => byId[id]).ToList();
            Save(configuration);
        }
    }

    public void BeginDeviceActivation(AppConfiguration configuration, SshDeviceConfiguration original)
    {
        lock (_gate)
        {
            var index = configuration.Devices.FindIndex(device =>
                string.Equals(device.Id, original.Id, StringComparison.OrdinalIgnoreCase));
            if (original.Enabled || index < 0 || !DeviceConfigurationComparer.AreEqual(configuration.Devices[index], original))
            {
                throw new CodexSyncBarException("SSH 장치 설정이 설치 도중 변경되어 활성화를 중단했습니다.");
            }

            configuration.Devices[index].Enabled = true;
            Save(configuration);
        }
    }

    public void RollbackDeviceActivation(AppConfiguration configuration, SshDeviceConfiguration original)
    {
        lock (_gate)
        {
            var index = configuration.Devices.FindIndex(device =>
                string.Equals(device.Id, original.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new CodexSyncBarException("복구할 SSH 장치 설정을 찾지 못했습니다.");
            }

            var activated = new SshDeviceConfiguration
            {
                Id = original.Id,
                CredentialId = original.CredentialId,
                DisplayName = original.DisplayName,
                Host = original.Host,
                Port = original.Port,
                Username = original.Username,
                Authentication = original.Authentication,
                IdentityFile = original.IdentityFile,
                CertificateFile = original.CertificateFile,
                HasPassword = original.HasPassword,
                HasKeyPassphrase = original.HasKeyPassphrase,
                Enabled = true,
            };
            if (DeviceConfigurationComparer.AreEqual(configuration.Devices[index], original))
            {
                return;
            }

            if (!DeviceConfigurationComparer.AreEqual(configuration.Devices[index], activated))
            {
                throw new CodexSyncBarException("SSH 장치 설정이 변경되어 자동 비활성화를 중단했습니다.");
            }

            configuration.Devices[index] = original;
            Save(configuration);
        }
    }

    public void ReconcilePendingAccounts(AppConfiguration configuration, AuthStore authStore)
    {
        lock (_gate)
        {
            var resolved = new List<AccountProfile>();
            var abandoned = new List<AccountProfile>();
            var changed = false;
            foreach (var account in configuration.Accounts)
            {
                if (!account.IsPending)
                {
                    resolved.Add(account);
                    continue;
                }

                try
                {
                    var credentials = authStore.ReadCredentials(account.Id);
                    changed |= account.IsPending || !string.Equals(account.Email, credentials.Email, StringComparison.Ordinal);
                    account.Email = credentials.Email;
                    account.IsPending = false;
                    resolved.Add(account);
                }
                catch (Exception error) when (error is CodexSyncBarException or AuthenticationRequiredException)
                {
                    abandoned.Add(account);
                    changed = true;
                }
            }

            if (resolved.Count == 0 && abandoned.Count > 0)
            {
                resolved.Add(abandoned[0]);
            }

            if (changed || configuration.Accounts.Count != resolved.Count)
            {
                configuration.Accounts = resolved;
                Save(configuration);
            }
        }
    }

    public void DiscoverExistingAccounts(AppConfiguration configuration, AuthStore authStore)
    {
        lock (_gate)
        {
            var changed = false;
            foreach (var profileId in authStore.ExistingProfileIds())
            {
                try
                {
                    var credentials = authStore.ReadCredentials(profileId);
                    var existing = configuration.Accounts.FirstOrDefault(account => account.Id == profileId);
                    if (existing is null)
                    {
                        configuration.Accounts.Add(new AccountProfile
                        {
                            Id = profileId,
                            Email = credentials.Email,
                            IsPending = false,
                        });
                        changed = true;
                    }
                    else if (existing.IsPending)
                    {
                        existing.Email = credentials.Email;
                        existing.IsPending = false;
                        existing.NeedsLogin = false;
                        changed = true;
                    }
                    else if (existing.NeedsLogin
                        || !string.Equals(existing.Email, credentials.Email, StringComparison.Ordinal))
                    {
                        existing.Email = credentials.Email;
                        existing.IsPending = false;
                        existing.NeedsLogin = false;
                        changed = true;
                    }

                    if (configuration.NextAccountId <= profileId)
                    {
                        configuration.NextAccountId = profileId + 1;
                        changed = true;
                    }
                }
                catch (AuthenticationRequiredException)
                {
                    // A malformed legacy profile is left for explicit repair.
                }
            }

            if (changed)
            {
                configuration.Accounts = configuration.Accounts
                    .OrderBy(account => account.Id)
                    .ToList();
                Save(configuration);
            }
        }
    }

    public void UpsertDevice(AppConfiguration configuration, SshDeviceConfiguration device)
    {
        ValidateDevice(device, checkCredentialFiles: true);
        var current = configuration.Devices.FindIndex(item =>
            string.Equals(item.Id, device.Id, StringComparison.OrdinalIgnoreCase));
        if (current >= 0)
        {
            device.CredentialId ??= configuration.Devices[current].CredentialId ?? Guid.NewGuid();
            configuration.Devices[current] = device;
        }
        else
        {
            device.CredentialId ??= Guid.NewGuid();
            configuration.Devices.Add(device);
        }

        Save(configuration);
    }

    public void RemoveDevice(AppConfiguration configuration, string deviceId)
    {
        configuration.Devices.RemoveAll(item =>
            string.Equals(item.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        Save(configuration);
    }

    private AccountProfile FindAccount(AppConfiguration configuration, int profileId) =>
        configuration.Accounts.FirstOrDefault(account => account.Id == profileId)
        ?? throw new CodexSyncBarException($"계정 {profileId}을(를) 찾을 수 없습니다.");

    private void SaveUnlocked(AppConfiguration configuration)
    {
        WindowsPathSafety.EnsureFile(_paths.ConfigurationFile, "설정 파일");
        var temporary = Path.Combine(_paths.StateRoot, $".config.{Guid.NewGuid():N}.tmp");
        var json = JsonSerializer.Serialize(configuration, JsonOptions);
        File.WriteAllText(temporary, json);
        try
        {
            File.Move(temporary, _paths.ConfigurationFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void Validate(
        AppConfiguration configuration,
        bool checkCredentialFiles)
    {
        if (configuration.SchemaVersion != AppConfiguration.CurrentSchemaVersion)
        {
            throw new CodexSyncBarException("지원하지 않는 설정 버전입니다.");
        }

        var ids = configuration.Accounts.Select(account => account.Id).ToArray();
        if (ids.Length == 0 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length
            || configuration.NextAccountId <= ids.Max())
        {
            throw new CodexSyncBarException("계정 설정이 손상되었습니다.");
        }

        if (configuration.Accounts.Any(account => string.IsNullOrWhiteSpace(account.Email)
            || account.Email.Any(char.IsControl)))
        {
            throw new CodexSyncBarException("계정 이메일 설정이 손상되었습니다.");
        }

        if (configuration.Accounts.Any(account => account.CustomAlias is not null
            && (account.CustomAlias.Length > AccountProfile.MaximumAliasLength
                || account.CustomAlias.Any(char.IsControl))))
        {
            throw new CodexSyncBarException("계정 별칭 설정이 손상되었습니다.");
        }

        var deviceIds = configuration.Devices.Select(device => device.Id).ToArray();
        if (deviceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != deviceIds.Length)
        {
            throw new CodexSyncBarException("중복된 SSH 장치 ID가 있습니다.");
        }

        var credentialIds = configuration.Devices
            .Where(device => device.CredentialId.HasValue)
            .Select(device => device.CredentialId!.Value)
            .ToArray();
        if (credentialIds.Distinct().Count() != credentialIds.Length)
        {
            throw new CodexSyncBarException("중복된 SSH 비밀 저장소 식별자가 있습니다.");
        }

        foreach (var device in configuration.Devices)
        {
            ValidateDevice(device, checkCredentialFiles);
        }
    }

    private static void ValidateDevice(
        SshDeviceConfiguration device,
        bool checkCredentialFiles)
    {
        if (string.IsNullOrWhiteSpace(device.Id)
            || !Regex.IsMatch(device.Id, "^[a-z0-9][a-z0-9-]{0,62}$"))
        {
            throw new CodexSyncBarException("장치 ID는 영문 소문자, 숫자, 하이픈만 사용할 수 있습니다.");
        }

        if (device.Id.Equals("windows", StringComparison.OrdinalIgnoreCase)
            || device.Id.Equals("macbook", StringComparison.OrdinalIgnoreCase))
        {
            throw new CodexSyncBarException("예약된 장치 ID는 사용할 수 없습니다.");
        }

        if (string.IsNullOrWhiteSpace(device.DisplayName) || device.DisplayName.Length > 64
            || device.DisplayName.Any(char.IsControl))
        {
            throw new CodexSyncBarException("장치 이름은 1~64자로 입력해 주세요.");
        }

        if (string.IsNullOrWhiteSpace(device.Host)
            || !Regex.IsMatch(device.Host, "^[A-Za-z0-9][A-Za-z0-9._:-]{0,252}$"))
        {
            throw new CodexSyncBarException("SSH 호스트 형식이 올바르지 않습니다.");
        }

        if (device.Port is < 1 or > 65_535)
        {
            throw new CodexSyncBarException("SSH 포트는 1~65535 사이여야 합니다.");
        }

        if (string.IsNullOrWhiteSpace(device.Username)
            || !Regex.IsMatch(device.Username, "^[A-Za-z0-9_][A-Za-z0-9._-]{0,63}$"))
        {
            throw new CodexSyncBarException("SSH 사용자 이름 형식이 올바르지 않습니다.");
        }

        if (device.Authentication is not ("openSSHConfig" or "privateKey" or "password"))
        {
            throw new CodexSyncBarException("지원하지 않는 SSH 인증 방식입니다.");
        }

        if (device.Authentication == "password"
            && (!device.HasPassword || device.CredentialId is null))
        {
            throw new CodexSyncBarException("SSH 비밀번호를 안전하게 저장해 주세요.");
        }

        if (device.HasKeyPassphrase && device.CredentialId is null)
        {
            throw new CodexSyncBarException("키 암호용 Windows 비밀 저장소 식별자가 없습니다.");
        }

        if (device.Authentication == "privateKey" && string.IsNullOrWhiteSpace(device.IdentityFile))
        {
            throw new CodexSyncBarException("개인 키 파일을 선택해 주세요.");
        }

        if (device.Authentication == "privateKey")
        {
            if (!Path.IsPathFullyQualified(device.IdentityFile!))
            {
                throw new CodexSyncBarException("개인 키 경로는 절대 경로여야 합니다.");
            }

            if (checkCredentialFiles)
            {
                ValidateCredentialFile(device.IdentityFile!, "개인 키");
            }

            if (!string.IsNullOrWhiteSpace(device.CertificateFile))
            {
                if (!Path.IsPathFullyQualified(device.CertificateFile!))
                {
                    throw new CodexSyncBarException("SSH 인증서 경로는 절대 경로여야 합니다.");
                }

                if (checkCredentialFiles)
                {
                    ValidateCredentialFile(device.CertificateFile!, "SSH 인증서");
                }
            }
        }
    }

    private static void ValidateCredentialFile(string path, string description)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new CodexSyncBarException($"{description} 경로는 절대 경로여야 합니다.");
        }

        WindowsPathSafety.EnsurePrivateFile(
            path,
            description,
            maximumBytes: 16 * 1024 * 1024);
        if (!File.Exists(path))
        {
            throw new CodexSyncBarException($"{description} 파일을 찾을 수 없습니다: {path}");
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new CodexSyncBarException($"{description}은 심볼릭 링크 또는 재분석 지점일 수 없습니다.");
        }
    }

    private static bool BackfillCredentialIds(AppConfiguration configuration)
    {
        var used = configuration.Devices
            .Where(device => device.CredentialId.HasValue)
            .Select(device => device.CredentialId!.Value)
            .ToHashSet();
        var changed = false;
        foreach (var device in configuration.Devices.Where(device => device.CredentialId is null))
        {
            var id = Guid.NewGuid();
            while (!used.Add(id))
            {
                id = Guid.NewGuid();
            }

            device.CredentialId = id;
            changed = true;
        }

        return changed;
    }
}
