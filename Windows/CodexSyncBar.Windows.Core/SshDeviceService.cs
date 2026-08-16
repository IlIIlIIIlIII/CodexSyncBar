using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

public sealed class SshDeviceService
{
    private const string ExpectedHelperVersion = "2.2.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private sealed record RemoteBundleFile(
        string LocalPath,
        string StageName,
        string DestinationRelativePath,
        string Mode);

    private readonly AuthStore _authStore;
    private readonly WindowsPaths _paths;
    private readonly WindowsSecretStore _secretStore;
    private readonly LogoutTransactionStore _logoutTransactions;
    private readonly SecretCleanupTransactionStore _secretCleanupTransactions;
    private readonly RemoteBootstrapTransactionStore _bootstrapTransactions;
    private readonly LocalSwitchService? _localSwitchService;

    public SshDeviceService(
        AuthStore authStore,
        WindowsPaths? paths = null,
        LocalSwitchService? localSwitchService = null)
    {
        _authStore = authStore;
        _paths = paths ?? new WindowsPaths();
        _secretStore = new WindowsSecretStore(_paths);
        _logoutTransactions = new LogoutTransactionStore(_paths);
        _secretCleanupTransactions = new SecretCleanupTransactionStore(_paths);
        _bootstrapTransactions = new RemoteBootstrapTransactionStore(_paths);
        _localSwitchService = localSwitchService;
    }

    public async Task<IReadOnlyList<string>> RecoverPendingBootstrapTransactionsAsync(
        AppConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var pending = new List<string>();
        foreach (var transaction in _bootstrapTransactions.LoadAll())
        {
            var device = configuration.Devices.FirstOrDefault(item =>
                string.Equals(item.Id, transaction.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                pending.Add($"{transaction.DeviceId}: 장치 설정 없음");
                continue;
            }

            if (!string.Equals(
                    EndpointFingerprint(device),
                    transaction.EndpointSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                pending.Add($"{device.DisplayLabel}: SSH endpoint가 변경됨");
                continue;
            }

            try
            {
                var secret = ResolveSecret(device);
                await RestoreRemoteBootstrapSnapshotAsync(
                    device,
                    transaction,
                    secret,
                    cancellationToken);
                _bootstrapTransactions.Delete(transaction);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                pending.Add($"{device.DisplayLabel}: {error.Message}");
            }
        }

        return pending;
    }

    public string BeginSecretCleanup(Guid credentialId) =>
        _secretCleanupTransactions.Begin(credentialId);

    public void CompleteSecretCleanup(string operation) =>
        _secretCleanupTransactions.Delete(operation);

    public IReadOnlyList<Guid> RecoverPendingSecretCleanup(AppConfiguration configuration)
    {
        var pending = new List<Guid>();
        foreach (var transaction in _secretCleanupTransactions.LoadAll())
        {
            var stillConfigured = configuration.Devices.Any(device =>
                device.CredentialId == transaction.CredentialId);
            if (stillConfigured)
            {
                _secretCleanupTransactions.Delete(
                    Path.Combine(
                        _secretCleanupTransactions.DirectoryPath,
                        transaction.Operation + ".json"));
                continue;
            }

            try
            {
                DeleteSecrets(transaction.CredentialId);
                _secretCleanupTransactions.Delete(
                    Path.Combine(
                        _secretCleanupTransactions.DirectoryPath,
                        transaction.Operation + ".json"));
            }
            catch
            {
                pending.Add(transaction.CredentialId);
            }
        }

        return pending;
    }

    public PreparedDeviceSave PrepareForSave(
        AppConfiguration configuration,
        SshDeviceConfiguration draft,
        string password,
        string passphrase,
        bool clearPassword = false,
        bool clearPassphrase = false)
    {
        var existing = configuration.Devices.FirstOrDefault(item =>
            string.Equals(item.Id, draft.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !existing.Enabled && draft.Enabled)
        {
            throw new CodexSyncBarException(
                "비활성화된 장치는 저장 후 ‘설치 및 활성화’로 먼저 연결을 검증해 주세요.");
        }

        var endpointChanged = existing is null
            || !DeviceConfigurationComparer.HasSameCredentialEndpoint(existing, draft);
        var passwordChanged = clearPassword || !string.IsNullOrEmpty(password);
        var passphraseChanged = clearPassphrase || !string.IsNullOrEmpty(passphrase);
        if (endpointChanged
            && existing?.HasKeyPassphrase == true
            && draft.Authentication == "privateKey"
            && string.IsNullOrEmpty(passphrase)
            && !clearPassphrase)
        {
            throw new CodexSyncBarException(
                "SSH 엔드포인트나 키를 변경할 때는 키 암호를 다시 입력하거나 저장된 키 암호 삭제를 선택해 주세요.");
        }
        var rotateCredential = existing is null
            || endpointChanged
            || passwordChanged
            || passphraseChanged
            || existing.CredentialId is null;
        var oldCredentialId = existing?.CredentialId;
        var prepared = DeviceConfigurationComparer.Clone(draft);
        prepared.CredentialId = rotateCredential
            ? Guid.NewGuid()
            : existing!.CredentialId;
        var cleanupIntents = new List<SecretCleanupIntent>();
        if (rotateCredential && prepared.CredentialId is { } targetCredentialId)
        {
            cleanupIntents.Add(new(
                targetCredentialId,
                _secretCleanupTransactions.Begin(targetCredentialId)));
        }

        if (oldCredentialId is { } oldId && oldId != prepared.CredentialId)
        {
            cleanupIntents.Add(new(
                oldId,
                _secretCleanupTransactions.Begin(oldId)));
        }

        if (prepared.Authentication == "password")
        {
            prepared.HasPassword = true;
            StoreOrCopySecret(
                prepared.CredentialId!.Value,
                oldCredentialId,
                "password",
                password,
                clearPassword,
                existing?.HasPassword == true && !endpointChanged);
        }
        else
        {
            prepared.HasPassword = false;
            DeleteSecret(prepared.CredentialId, "password");
        }

        if (prepared.Authentication == "privateKey")
        {
            var shouldHavePassphrase = !clearPassphrase
                && (!string.IsNullOrEmpty(passphrase) || existing?.HasKeyPassphrase == true);
            prepared.HasKeyPassphrase = shouldHavePassphrase;
            if (shouldHavePassphrase)
            {
                StoreOrCopySecret(
                    prepared.CredentialId!.Value,
                    oldCredentialId,
                    "passphrase",
                    passphrase,
                    clearPassphrase,
                    existing?.HasKeyPassphrase == true && !endpointChanged);
            }
            else
            {
                DeleteSecret(prepared.CredentialId, "passphrase");
            }
        }
        else
        {
            prepared.HasKeyPassphrase = false;
            DeleteSecret(prepared.CredentialId, "passphrase");
        }

        // A changed endpoint or secret must be tested before it becomes part of
        // the automatic sync set. Display-only edits can keep their enablement.
        var requiresActivation = existing is null
            || endpointChanged
            || passwordChanged
            || passphraseChanged;
        if (requiresActivation)
        {
            prepared.Enabled = false;
        }

        return new PreparedDeviceSave(
            prepared,
            oldCredentialId is { } && oldCredentialId != prepared.CredentialId ? oldCredentialId : null,
            requiresActivation,
            cleanupIntents);
    }

    public void DeleteSecrets(Guid credentialId)
    {
        var key = credentialId.ToString("D");
        _secretStore.Delete($"{key}.password");
        _secretStore.Delete($"{key}.passphrase");
    }

    public void SaveSecrets(
        SshDeviceConfiguration device,
        string password,
        string passphrase,
        bool clearPassword = false,
        bool clearPassphrase = false)
    {
        var credentialId = device.CredentialId
            ?? throw new CodexSyncBarException("SSH 비밀 저장소 식별자가 없습니다.");
        var key = credentialId.ToString("D");
        if (clearPassword || device.Authentication != "password")
        {
            _secretStore.Delete($"{key}.password");
            device.HasPassword = false;
        }
        else if (!string.IsNullOrEmpty(password))
        {
            _secretStore.Save(password, $"{key}.password");
            device.HasPassword = true;
        }
        else if (device.HasPassword && _secretStore.Read($"{key}.password") is null)
        {
            throw new CodexSyncBarException("저장된 SSH 비밀번호를 찾지 못했습니다.");
        }

        if (clearPassphrase || device.Authentication != "privateKey")
        {
            _secretStore.Delete($"{key}.passphrase");
            device.HasKeyPassphrase = false;
        }
        else if (!string.IsNullOrEmpty(passphrase))
        {
            _secretStore.Save(passphrase, $"{key}.passphrase");
            device.HasKeyPassphrase = true;
        }
        else if (device.HasKeyPassphrase && _secretStore.Read($"{key}.passphrase") is null)
        {
            throw new CodexSyncBarException("저장된 SSH 키 암호를 찾지 못했습니다.");
        }
    }

    public void DeleteSecrets(SshDeviceConfiguration device)
    {
        if (device.CredentialId is not { } credentialId)
        {
            return;
        }

        DeleteSecrets(credentialId);
    }

    private void StoreOrCopySecret(
        Guid targetCredentialId,
        Guid? sourceCredentialId,
        string suffix,
        string enteredSecret,
        bool clear,
        bool sourceHasSecret)
    {
        var targetKey = targetCredentialId.ToString("D");
        if (clear)
        {
            throw new CodexSyncBarException(
                suffix == "password"
                    ? "비밀번호 인증을 사용하려면 SSH 비밀번호를 입력해 주세요."
                    : "키 암호를 삭제하려면 다른 인증 방식을 선택해 주세요.");
        }

        if (!string.IsNullOrEmpty(enteredSecret))
        {
            _secretStore.Save(enteredSecret, $"{targetKey}.{suffix}");
            return;
        }

        if (sourceHasSecret && sourceCredentialId is { } sourceId)
        {
            var sourceSecret = _secretStore.Read($"{sourceId:D}.{suffix}");
            if (!string.IsNullOrEmpty(sourceSecret))
            {
                _secretStore.Save(sourceSecret, $"{targetKey}.{suffix}");
                return;
            }
        }

        throw new CodexSyncBarException(
            suffix == "password"
                ? "SSH 비밀번호를 입력하거나 기존 저장값을 유지해 주세요."
                : "개인 키 암호를 입력하거나 기존 저장값을 유지해 주세요.");
    }

    private void DeleteSecret(Guid? credentialId, string suffix)
    {
        if (credentialId is { } id)
        {
            _secretStore.Delete($"{id:D}.{suffix}");
        }
    }

    public async Task<SshTestResult> TestAsync(
        SshDeviceConfiguration device,
        CancellationToken cancellationToken = default)
    {
        var secret = ResolveSecret(device);
        var result = await RunSshAsync(
            device,
            ["~/.local/bin/gpt-switch", "__node", "version"],
            cancellationToken,
            TimeSpan.FromSeconds(15),
            secret);
        var version = result.StandardOutput.Trim();
        var message = result.CombinedOutput.Trim();
        var isValid = result.ExitCode == 0
            && string.Equals(version, ExpectedHelperVersion, StringComparison.Ordinal);
        return new SshTestResult(
            device.Id,
            isValid,
            isValid
                ? $"SSH 연결과 SyncBar helper {version}을 확인했습니다."
                : result.ExitCode == 0
                    ? $"원격 helper 버전이 다릅니다. 필요 버전 {ExpectedHelperVersion}, 원격 버전 {version}."
                    : message);
    }

    public async Task<SshTestResult> TestConnectionAsync(
        SshDeviceConfiguration device,
        CancellationToken cancellationToken = default)
    {
        var secret = ResolveSecret(device);
        var result = await RunSshAsync(
            device,
            ["echo", "codex-syncbar"],
            cancellationToken,
            TimeSpan.FromSeconds(15),
            secret);
        var message = result.CombinedOutput.Trim();
        return new SshTestResult(
            device.Id,
            result.ExitCode == 0 && message.Contains("codex-syncbar", StringComparison.Ordinal),
            result.ExitCode == 0 ? "SSH 연결이 정상입니다." : message);
    }

    public async Task<IReadOnlyList<DeviceStatus>> FetchStatusesAsync(
        AppConfiguration configuration,
        int? activeProfileId,
        CancellationToken cancellationToken = default)
    {
        var statuses = new List<DeviceStatus>
        {
            new(
                "windows",
                "이 Windows PC",
                activeProfileId,
                null,
                "local",
                "ready",
                true),
        };

        foreach (var device in configuration.Devices)
        {
            if (!device.Enabled)
            {
                statuses.Add(new DeviceStatus(
                    device.Id,
                    device.DisplayLabel,
                    null,
                    null,
                    device.Authentication,
                    "disabled",
                    false,
                    "비활성화됨 · 설치 및 활성화가 필요합니다."));
                continue;
            }

            try
            {
                statuses.Add(await FetchRemoteStatusAsync(device, activeProfileId, cancellationToken));
            }
            catch (Exception error) when (
                error is CodexSyncBarException
                or InvalidOperationException
                or IOException
                or TimeoutException
                or System.ComponentModel.Win32Exception)
            {
                statuses.Add(new DeviceStatus(
                    device.Id,
                    device.DisplayLabel,
                    null,
                    null,
                    device.Authentication,
                    "unreachable",
                    false,
                    error.Message));
            }
        }

        return statuses;
    }

    private async Task<DeviceStatus> FetchRemoteStatusAsync(
        SshDeviceConfiguration device,
        int? activeProfileId,
        CancellationToken cancellationToken)
    {
        var result = await RunSshAsync(
            device,
            ["~/.local/bin/gpt-switch", "__node", "status"],
            cancellationToken,
            TimeSpan.FromSeconds(20),
            ResolveSecret(device));
        if (result.ExitCode != 0)
        {
            throw new CodexSyncBarException(
                result.CombinedOutput.Trim().Length == 0
                    ? "원격 SyncBar helper를 찾지 못했습니다. 먼저 설치 및 활성화를 실행해 주세요."
                    : result.CombinedOutput.Trim());
        }

        var fields = result.StandardOutput
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        var active = fields.TryGetValue("active", out var activeValue)
            && int.TryParse(activeValue, out var profileId)
            ? profileId
            : (int?)null;
        var fingerprint = fields.GetValueOrDefault("fingerprint");
        var cliState = fields.GetValueOrDefault("cli");
        return new DeviceStatus(
            device.Id,
            device.DisplayLabel,
            active,
            fingerprint is "unknown" or null ? null : fingerprint,
            fields.GetValueOrDefault("auth_mode") is { } auth && auth != "unknown"
                ? auth
                : device.Authentication,
            cliState is "unknown" or null ? "unavailable" : cliState,
            active is not null && (activeProfileId is null || active == activeProfileId),
                    active is not null && activeProfileId is not null && active != activeProfileId
                ? $"원격 활성 계정 {active} · 이 PC의 계정 {activeProfileId}와 다름"
                : null);
    }

    public async Task SyncAuthAsync(
        SshDeviceConfiguration device,
        int profileId,
        CancellationToken cancellationToken = default)
    {
        var secret = ResolveSecret(device);
        var accessOnly = _authStore.CreateAccessOnlyCopy(profileId);
        await InstallRemoteHelpersAsync(device, secret, cancellationToken);
        var install = await RunSshWithInputAsync(
            device,
            [
                "~/.local/bin/gpt-switch",
                "__node",
                "install-access",
                profileId.ToString(),
                Fingerprint(accessOnly.Tokens.AccountId!),
                Fingerprint(accessOnly.Tokens.AccessToken!),
            ],
            JsonSerializer.Serialize(accessOnly, JsonOptions),
            secret,
            cancellationToken);
        EnsureRemoteSuccess(device, install, $"계정 {profileId} 원격 인증 동기화");
    }

    public async Task SyncEnabledAsync(
        AppConfiguration configuration,
        int profileId,
        CancellationToken cancellationToken = default)
    {
        var failures = await SyncProfileAsync(configuration, profileId, cancellationToken);
        if (failures.Count > 0)
        {
            throw new CodexSyncBarException(string.Join(" · ", failures));
        }
    }

    /// <summary>
    /// Applies one account to the local Windows Codex installation and every
    /// enabled SSH node as one controller operation. Remote nodes are
    /// preflighted and the target access generation is installed before any
    /// active profile changes. If a later node or the local switch fails, every
    /// node that may have changed is switched back to its recorded profile.
    /// </summary>
    public async Task SwitchAllAsync(
        AppConfiguration configuration,
        int profileId,
        CancellationToken cancellationToken = default)
    {
        if (_localSwitchService is null)
        {
            throw new CodexSyncBarException("로컬 계정 전환 서비스가 연결되지 않았습니다.");
        }

        _ = _authStore.ReadCredentials(profileId);
        var enabledDevices = configuration.Devices
            .Where(item => item.Enabled)
            .ToArray();
        var previousLocalProfileId = _localSwitchService.GetActiveProfileId(configuration.Accounts);
        var previousLocalAuth = _authStore.ReadActiveAuth();
        var remoteStates = new List<RemoteSwitchState>();
        var localChanged = false;

        try
        {
            foreach (var device in enabledDevices)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Keep the remote access-only profile current before asking the
                // node to switch. This also makes the action useful immediately
                // after a new account login, without waiting for the six-hour
                // maintenance pass.
                await SyncAuthAsync(device, profileId, cancellationToken);
                var secret = ResolveSecret(device);
                var preflight = await RunRemoteActionAsync(
                    device,
                    ["~/.local/bin/gpt-switch", "__node", "preflight", profileId.ToString()],
                    secret,
                    cancellationToken);
                EnsureRemoteSuccess(device, preflight, "원격 계정 전환 사전 검증");

                var fields = ParseFields(preflight.StandardOutput);
                if (!fields.TryGetValue("active", out var activeValue)
                    || !int.TryParse(activeValue, out var previousProfileId))
                {
                    throw new CodexSyncBarException(
                        $"{device.DisplayLabel}의 현재 활성 계정을 해석하지 못했습니다.");
                }

                remoteStates.Add(new RemoteSwitchState(device, previousProfileId));
            }

            foreach (var state in remoteStates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (state.PreviousProfileId == profileId)
                {
                    continue;
                }

                state.SwitchAttempted = true;
                var result = await RunRemoteActionAsync(
                    state.Device,
                    ["~/.local/bin/gpt-switch", "__node", "switch", profileId.ToString(), "1"],
                    ResolveSecret(state.Device),
                    cancellationToken);
                if (result.ExitCode == 0)
                {
                    state.Changed = true;
                    continue;
                }

                // The helper can have committed the auth and then failed while
                // stopping a client. Verify before deciding whether rollback is
                // required, matching the POSIX controller contract.
                var verified = await RunRemoteActionAsync(
                    state.Device,
                    ["~/.local/bin/gpt-switch", "__node", "verify", profileId.ToString()],
                    ResolveSecret(state.Device),
                    CancellationToken.None);
                if (verified.ExitCode == 0)
                {
                    state.Changed = true;
                    continue;
                }

                throw new CodexSyncBarException(
                    $"{state.Device.DisplayLabel} 계정 전환에 실패했습니다: {TrimRemoteOutput(result)}");
            }

            await _localSwitchService.SwitchAsync(profileId, cancellationToken);
            localChanged = true;

            if (_localSwitchService.GetActiveProfileId(configuration.Accounts) != profileId)
            {
                throw new CodexSyncBarException("Windows 로컬 활성 계정 검증에 실패했습니다.");
            }

            foreach (var state in remoteStates)
            {
                var verified = await RunRemoteActionAsync(
                    state.Device,
                    ["~/.local/bin/gpt-switch", "__node", "verify", profileId.ToString()],
                    ResolveSecret(state.Device),
                    cancellationToken);
                EnsureRemoteSuccess(state.Device, verified, "원격 계정 전환 검증");
            }
        }
        catch (Exception error)
        {
            var recoveryErrors = new List<string>();

            if (localChanged)
            {
                try
                {
                    if (previousLocalProfileId is { } previousProfileId)
                    {
                        await _localSwitchService.SwitchAsync(previousProfileId, CancellationToken.None);
                    }
                    else
                    {
                        _authStore.RestoreActive(previousLocalAuth);
                    }
                }
                catch (Exception recoveryError)
                {
                    recoveryErrors.Add($"Windows 로컬 복구: {recoveryError.Message}");
                }
            }

            for (var index = remoteStates.Count - 1; index >= 0; index--)
            {
                var state = remoteStates[index];
                if (!state.SwitchAttempted || state.PreviousProfileId == profileId)
                {
                    continue;
                }

                try
                {
                    var rollback = await RunRemoteActionAsync(
                        state.Device,
                        [
                            "~/.local/bin/gpt-switch", "__node", "switch",
                            state.PreviousProfileId.ToString(), "1",
                        ],
                        ResolveSecret(state.Device),
                        CancellationToken.None);
                    EnsureRemoteSuccess(state.Device, rollback, "원격 계정 전환 복구");
                    var verified = await RunRemoteActionAsync(
                        state.Device,
                        [
                            "~/.local/bin/gpt-switch", "__node", "verify",
                            state.PreviousProfileId.ToString(),
                        ],
                        ResolveSecret(state.Device),
                        CancellationToken.None);
                    EnsureRemoteSuccess(state.Device, verified, "원격 계정 전환 복구 검증");
                }
                catch (Exception recoveryError)
                {
                    recoveryErrors.Add($"{state.Device.DisplayLabel}: {recoveryError.Message}");
                }
            }

            var suffix = recoveryErrors.Count == 0
                ? "변경된 장치를 이전 계정으로 복구했습니다."
                : $"일부 장치의 복구가 필요합니다: {string.Join(" · ", recoveryErrors)}";
            throw new CodexSyncBarException(
                $"계정 전환에 실패했습니다. {suffix}",
                error);
        }
    }

    public async Task<IReadOnlyList<string>> SyncProfileAsync(
        AppConfiguration configuration,
        int profileId,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        foreach (var device in configuration.Devices.Where(item => item.Enabled))
        {
            try
            {
                await SyncAuthAsync(device, profileId, cancellationToken);
            }
            catch (Exception error) when (error is CodexSyncBarException or IOException)
            {
                failures.Add($"{device.DisplayLabel}/계정 {profileId}: {error.Message}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add($"{device.DisplayLabel}/계정 {profileId}: 원격 인증 동기화 시간이 초과되었습니다.");
            }
        }

        return failures;
    }

    public async Task<IReadOnlyList<string>> SyncAllProfilesAsync(
        AppConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var profiles = configuration.Accounts
            .Where(account => !account.IsPending
                && !account.NeedsLogin
                && _authStore.ProfileArtifactExists(account.Id))
            .Select(account => account.Id)
            .ToArray();
        var failures = new List<string>();
        foreach (var device in configuration.Devices.Where(item => item.Enabled))
        {
            foreach (var profileId in profiles)
            {
                var profileFailures = await SyncProfileOnDeviceAsync(
                    device,
                    profileId,
                    cancellationToken);
                failures.AddRange(profileFailures);
            }
        }

        return failures;
    }

    private async Task<IReadOnlyList<string>> SyncProfileOnDeviceAsync(
        SshDeviceConfiguration device,
        int profileId,
        CancellationToken cancellationToken)
    {
        try
        {
            await SyncAuthAsync(device, profileId, cancellationToken);
            return [];
        }
        catch (Exception error) when (error is CodexSyncBarException or IOException)
        {
            return [$"{device.DisplayLabel}/계정 {profileId}: {error.Message}"];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [$"{device.DisplayLabel}/계정 {profileId}: 원격 인증 동기화 시간이 초과되었습니다."];
        }
    }

    private sealed class RemoteSwitchState(
        SshDeviceConfiguration device,
        int previousProfileId)
    {
        public SshDeviceConfiguration Device { get; } = device;

        public int PreviousProfileId { get; } = previousProfileId;

        public bool SwitchAttempted { get; set; }

        public bool Changed { get; set; }
    }

    public async Task<SshBootstrapResult> BootstrapAsync(
        AppConfiguration configuration,
        SshDeviceConfiguration device,
        int? activeProfileId,
        CancellationToken cancellationToken = default)
    {
        var profiles = configuration.Accounts
            .Where(account => !account.IsPending
                && !account.NeedsLogin
                && _authStore.ProfileArtifactExists(account.Id))
            .Select(account => account.Id)
            .ToArray();
        if (profiles.Length == 0)
        {
            throw new CodexSyncBarException("원격 장치에 설치할 로그인된 계정이 없습니다.");
        }

        var secret = ResolveSecret(device);
        RemoteBootstrapTransaction? transaction = null;
        try
        {
            transaction = await BeginRemoteBootstrapTransactionAsync(
                device,
                secret,
                cancellationToken);
            await InstallRemoteHelpersAsync(device, secret, cancellationToken);
            foreach (var profileId in profiles)
            {
                var accessOnly = _authStore.CreateAccessOnlyCopy(profileId);
                var install = await RunSshWithInputAsync(
                    device,
                    [
                        "~/.local/bin/gpt-switch",
                        "__node",
                        "install-access",
                        profileId.ToString(),
                        Fingerprint(accessOnly.Tokens.AccountId!),
                        Fingerprint(accessOnly.Tokens.AccessToken!),
                    ],
                    JsonSerializer.Serialize(accessOnly, JsonOptions),
                    secret,
                    cancellationToken);
                EnsureRemoteSuccess(device, install, $"계정 {profileId} 원격 설치");
            }

            var selectedProfileId = activeProfileId is { } active && profiles.Contains(active)
                ? active
                : profiles[0];
            var initialize = await RunRemoteActionAsync(
                device,
                ["~/.local/bin/gpt-switch", "__node", "initialize", selectedProfileId.ToString()],
                secret,
                cancellationToken);
            EnsureRemoteSuccess(device, initialize, "원격 활성 계정 초기화");
            var verification = await RunRemoteActionAsync(
                device,
                ["~/.local/bin/gpt-switch", "__node", "verify", selectedProfileId.ToString()],
                secret,
                cancellationToken);
            if (verification.ExitCode != 0)
            {
                throw new CodexSyncBarException(
                    $"{device.DisplayLabel} 원격 Codex 인증 검증에 실패했습니다: {verification.CombinedOutput.Trim()}");
            }

            _bootstrapTransactions.Delete(transaction);
            transaction = null;
            return new SshBootstrapResult(device.Id, selectedProfileId, profiles.Length);
        }
        catch (Exception error)
        {
            if (transaction is null)
            {
                throw;
            }

            try
            {
                await RestoreRemoteBootstrapSnapshotAsync(
                    device,
                    transaction,
                    secret,
                    CancellationToken.None);
                _bootstrapTransactions.Delete(transaction);
            }
            catch (Exception recoveryError)
            {
                throw new CodexSyncBarException(
                    $"{device.DisplayLabel} 부트스트랩에 실패했고 원격 이전 상태도 자동 복구하지 못했습니다. 앱을 다시 열어 복구를 재시도해 주세요: {recoveryError.Message}",
                    error);
            }

            throw;
        }
    }

    public async Task<CursorRemoteResult> ProvisionCursorAsync(
        SshDeviceConfiguration device,
        CursorBridgePreferences preferences,
        CursorModelCatalog catalog,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var validatedKey = CursorApiKeyValidator.Validate(apiKey);
        var effectiveModel = preferences.Model.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? catalog.SuggestedModel
            : preferences.Model;
        if (!catalog.Variants.Any(item => item.Slug == effectiveModel))
        {
            throw new CodexSyncBarException(
                $"현재 Cursor 계정에서 사용할 수 없는 모델입니다: {effectiveModel}");
        }

        var secret = ResolveSecret(device);
        await InstallRemoteHelpersAsync(device, secret, cancellationToken);
        await InstallRemoteCursorHelpersAsync(device, secret, cancellationToken);
        var environment = catalog.BuildBridgeEnvironment();
        var payload = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["apiKey"] = validatedKey,
            ["bridgeToken"] = preferences.BridgeToken,
            ["model"] = effectiveModel,
            ["port"] = preferences.Port,
            ["models"] = JsonNode.Parse(environment.AllowedModelsJson),
            ["modelParameters"] = JsonNode.Parse(environment.ModelParametersJson),
            ["modelRoutes"] = JsonNode.Parse(environment.ModelRoutesJson),
        };
        var request = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var result = await RunSshWithInputAsync(
            device,
            ["~/.local/bin/gpt-switch", "__node", "cursor-provision"],
            request,
            secret,
            cancellationToken);
        if (result.ExitCode != 0
            || !result.CombinedOutput.Contains("cursor=provisioned result=ok", StringComparison.Ordinal))
        {
            throw new CodexSyncBarException(
                $"{device.DisplayLabel}에 Cursor provider를 설치하지 못했습니다.");
        }

        return new CursorRemoteResult(device.Id, "provisioned", effectiveModel);
    }

    public async Task<CursorRemoteResult> DeprovisionCursorAsync(
        SshDeviceConfiguration device,
        CancellationToken cancellationToken = default)
    {
        var secret = ResolveSecret(device);
        await InstallRemoteHelpersAsync(device, secret, cancellationToken);
        await InstallRemoteCursorHelpersAsync(device, secret, cancellationToken);
        var result = await RunRemoteActionAsync(
            device,
            ["~/.local/bin/gpt-switch", "__node", "cursor-deprovision"],
            secret,
            cancellationToken);
        if (result.ExitCode != 0
            || !result.CombinedOutput.Contains("cursor=deprovisioned result=ok", StringComparison.Ordinal))
        {
            throw new CodexSyncBarException(
                $"{device.DisplayLabel}에서 Cursor provider를 제거하지 못했습니다.");
        }

        return new CursorRemoteResult(device.Id, "deprovisioned", null);
    }

    public async Task LogoutAsync(
        AppConfiguration configuration,
        int profileId,
        int fallbackProfileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == fallbackProfileId)
        {
            throw new CodexSyncBarException("로그아웃 fallback 계정은 다른 계정이어야 합니다.");
        }

        _ = _authStore.ReadCredentials(profileId);
        _ = _authStore.ReadCredentials(fallbackProfileId);
        var operation = $"logout_{profileId}_{Guid.NewGuid():N}";
        var enabledDevices = configuration.Devices
            .Where(item => item.Enabled)
            .ToArray();
        var manifest = new LogoutTransactionManifest
        {
            Operation = operation,
            ProfileId = profileId,
            FallbackProfileId = fallbackProfileId,
            DeviceIds = enabledDevices.Select(device => device.Id).ToList(),
        };
        _paths.EnsureDirectories();
        Directory.CreateDirectory(_logoutTransactions.DirectoryFor(operation));
        File.Copy(
            _authStore.ProfileAuthFile(profileId),
            _logoutTransactions.BackupFileFor(operation),
            overwrite: true);
        _logoutTransactions.Save(manifest);

        try
        {
            foreach (var device in enabledDevices)
            {
                var secret = ResolveSecret(device);
                await InstallRemoteHelpersAsync(device, secret, cancellationToken);
                manifest.FallbackSwitchAttemptedDeviceIds.Add(device.Id);
                _logoutTransactions.Save(manifest);
                var activateFallback = await RunRemoteActionAsync(
                    device,
                    ["~/.local/bin/gpt-switch", "__node", "switch", fallbackProfileId.ToString(), "1"],
                    secret,
                    cancellationToken);
                EnsureRemoteSuccess(device, activateFallback, "원격 fallback 계정 전환");
                manifest.FallbackSwitchAttemptedDeviceIds.RemoveAll(id =>
                    string.Equals(id, device.Id, StringComparison.OrdinalIgnoreCase));
                manifest.FallbackSwitchedDeviceIds.Add(device.Id);
                _logoutTransactions.Save(manifest);
                var preflight = await RunRemoteActionAsync(
                    device,
                    [
                        "~/.local/bin/gpt-switch", "__node", "logout-preflight",
                        profileId.ToString(), fallbackProfileId.ToString(),
                    ],
                    secret,
                    cancellationToken);
                EnsureRemoteSuccess(device, preflight, "원격 로그아웃 사전 검증");
                manifest.StageAttemptedDeviceIds.Add(device.Id);
                _logoutTransactions.Save(manifest);
                var stage = await RunRemoteActionAsync(
                    device,
                    [
                        "~/.local/bin/gpt-switch", "__node", "logout-stage",
                        operation, profileId.ToString(), fallbackProfileId.ToString(),
                    ],
                    secret,
                    cancellationToken);
                EnsureRemoteSuccess(device, stage, "원격 로그아웃 준비");
                manifest.StageAttemptedDeviceIds.RemoveAll(id =>
                    string.Equals(id, device.Id, StringComparison.OrdinalIgnoreCase));
                manifest.StagedDeviceIds.Add(device.Id);
                _logoutTransactions.Save(manifest);
            }

            manifest.LocalFallbackSwitched = true;
            _logoutTransactions.Save(manifest);
            if (_localSwitchService is not null)
            {
                await _localSwitchService.SwitchAsync(fallbackProfileId, cancellationToken);
            }
            else
            {
                _authStore.SwitchActive(fallbackProfileId);
            }

            _authStore.DeleteProfile(profileId);
            manifest.State = "committing";
            _logoutTransactions.Save(manifest);

            if (!await TryCommitLogoutTransactionAsync(configuration, manifest, cancellationToken))
            {
                throw new CodexSyncBarException(
                    $"계정 로그아웃 검증이 완료되지 않았습니다. 복구 기록을 보존했습니다: {_logoutTransactions.DirectoryFor(operation)}");
            }

            _logoutTransactions.Delete(operation);
        }
        catch (Exception error)
        {
            manifest.LastError = error.Message;
            if (!File.Exists(_authStore.ProfileAuthFile(profileId)))
            {
                manifest.State = "committing";
            }

            try
            {
                _logoutTransactions.Save(manifest);
            }
            catch
            {
                // Keep the original failure; the already-created backup is
                // still safer than deleting a credential without a journal.
            }

            if (manifest.State == "committing")
            {
                if (await TryCommitLogoutTransactionAsync(configuration, manifest, CancellationToken.None))
                {
                    _logoutTransactions.Delete(operation);
                    return;
                }

                throw new CodexSyncBarException(
                    $"계정 로그아웃이 일부 장치에서 중단되었습니다. 복구 기록을 보존했습니다: {_logoutTransactions.DirectoryFor(operation)}",
                    error);
            }

            if (await TryRestoreLogoutTransactionAsync(configuration, manifest, CancellationToken.None))
            {
                if (await TryRestoreLocalLogoutTransactionAsync(manifest, CancellationToken.None))
                {
                    _logoutTransactions.Delete(operation);
                    throw;
                }
            }

            throw new CodexSyncBarException(
                $"계정 로그아웃이 중단되었습니다. 원격 복구 기록을 보존했습니다: {_logoutTransactions.DirectoryFor(operation)}",
                error);
        }
    }

    public async Task<LogoutRecoveryResult> RecoverPendingLogoutsAsync(
        AppConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var pending = new List<string>();
        var completedProfiles = new List<int>();
        foreach (var manifest in _logoutTransactions.LoadAll())
        {
            try
            {
                if (manifest.State == "staging"
                    && !File.Exists(_authStore.ProfileAuthFile(manifest.ProfileId)))
                {
                    manifest.State = "committing";
                    _logoutTransactions.Save(manifest);
                }

                if (manifest.State == "committing")
                {
                    if (!await TryCommitLogoutTransactionAsync(configuration, manifest, cancellationToken))
                    {
                        pending.Add(manifest.Operation);
                        continue;
                    }

                    _logoutTransactions.Delete(manifest.Operation);
                    completedProfiles.Add(manifest.ProfileId);
                    continue;
                }

                if (!await TryRestoreLogoutTransactionAsync(configuration, manifest, cancellationToken))
                {
                    pending.Add(manifest.Operation);
                    continue;
                }

                if (await TryRestoreLocalLogoutTransactionAsync(manifest, cancellationToken))
                {
                    _logoutTransactions.Delete(manifest.Operation);
                }
                else
                {
                    pending.Add(manifest.Operation);
                }
            }
            catch (Exception error)
            {
                manifest.LastError = error.Message;
                try
                {
                    _logoutTransactions.Save(manifest);
                }
                catch
                {
                }

                pending.Add(manifest.Operation);
            }
        }

        return new LogoutRecoveryResult(pending, completedProfiles);
    }

    private async Task<bool> TryCommitLogoutTransactionAsync(
        AppConfiguration configuration,
        LogoutTransactionManifest manifest,
        CancellationToken cancellationToken)
    {
        foreach (var deviceId in manifest.StagedDeviceIds.ToArray())
        {
            var device = configuration.Devices.FirstOrDefault(item =>
                string.Equals(item.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                return false;
            }

            var secret = ResolveSecret(device);
            await InstallRemoteHelpersAsync(device, secret, cancellationToken);
            if (!manifest.CommittedDeviceIds.Contains(device.Id, StringComparer.OrdinalIgnoreCase))
            {
                var verify = await RunRemoteActionAsync(
                    device,
                    [
                        "~/.local/bin/gpt-switch", "__node", "logout-verify",
                        manifest.Operation,
                        manifest.ProfileId.ToString(),
                        manifest.FallbackProfileId.ToString(),
                    ],
                    secret,
                    cancellationToken);
                if (verify.ExitCode != 0)
                {
                    return false;
                }

                var commit = await RunRemoteActionAsync(
                    device,
                    [
                        "~/.local/bin/gpt-switch", "__node", "logout-commit",
                        manifest.Operation,
                        manifest.ProfileId.ToString(),
                        manifest.FallbackProfileId.ToString(),
                    ],
                    secret,
                    cancellationToken);
                if (commit.ExitCode != 0)
                {
                    return false;
                }

                manifest.CommittedDeviceIds.Add(device.Id);
                _logoutTransactions.Save(manifest);
            }
        }

        return manifest.CommittedDeviceIds.Count == manifest.StagedDeviceIds.Count;
    }

    private async Task<bool> TryRestoreLogoutTransactionAsync(
        AppConfiguration configuration,
        LogoutTransactionManifest manifest,
        CancellationToken cancellationToken)
    {
        var restored = true;
        var stagedDevices = manifest.StageAttemptedDeviceIds
            .Concat(manifest.StagedDeviceIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var deviceId in stagedDevices)
        {
            var device = configuration.Devices.FirstOrDefault(item =>
                string.Equals(item.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                restored = false;
                continue;
            }

            try
            {
                var secret = ResolveSecret(device);
                await InstallRemoteHelpersAsync(device, secret, cancellationToken);
                var result = await RunRemoteActionAsync(
                    device,
                    [
                        "~/.local/bin/gpt-switch", "__node", "logout-restore",
                        manifest.Operation,
                        manifest.ProfileId.ToString(),
                    ],
                    secret,
                    cancellationToken);
                restored &= result.ExitCode == 0;
            }
            catch
            {
                restored = false;
            }
        }

        var fallbackDevices = manifest.FallbackSwitchAttemptedDeviceIds
            .Concat(manifest.FallbackSwitchedDeviceIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var deviceId in fallbackDevices)
        {
            var device = configuration.Devices.FirstOrDefault(item =>
                string.Equals(item.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                restored = false;
                continue;
            }

            try
            {
                var secret = ResolveSecret(device);
                var switchBack = await RunRemoteActionAsync(
                    device,
                    [
                        "~/.local/bin/gpt-switch", "__node", "switch",
                        manifest.ProfileId.ToString(), "1",
                    ],
                    secret,
                    cancellationToken);
                if (switchBack.ExitCode != 0)
                {
                    restored = false;
                    continue;
                }

                var verify = await RunRemoteActionAsync(
                    device,
                    [
                        "~/.local/bin/gpt-switch", "__node", "verify",
                        manifest.ProfileId.ToString(),
                    ],
                    secret,
                    cancellationToken);
                restored &= verify.ExitCode == 0;
            }
            catch
            {
                restored = false;
            }
        }

        return restored;
    }

    private void RestoreLocalProfileFromBackup(LogoutTransactionManifest manifest)
    {
        var backup = _logoutTransactions.BackupFileFor(manifest.Operation);
        if (!File.Exists(backup))
        {
            throw new CodexSyncBarException("로그아웃 복구 백업이 없어 인증을 복원할 수 없습니다.");
        }

        _authStore.ImportAuth(backup, manifest.ProfileId, replaceExisting: true);
    }

    private async Task<bool> TryRestoreLocalLogoutTransactionAsync(
        LogoutTransactionManifest manifest,
        CancellationToken cancellationToken)
    {
        try
        {
            RestoreLocalProfileFromBackup(manifest);
            if (!manifest.LocalFallbackSwitched)
            {
                return true;
            }

            var target = _authStore.ReadCredentials(manifest.ProfileId);
            if (string.Equals(_authStore.ReadActiveAccountId(), target.AccountId, StringComparison.Ordinal))
            {
                return true;
            }

            if (_localSwitchService is not null)
            {
                await _localSwitchService.SwitchAsync(manifest.ProfileId, cancellationToken);
            }
            else
            {
                _authStore.SwitchActive(manifest.ProfileId);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<RemoteBootstrapTransaction> BeginRemoteBootstrapTransactionAsync(
        SshDeviceConfiguration device,
        string? secret,
        CancellationToken cancellationToken)
    {
        var snapshot = await RunSshWithInputAsync(
            device,
            ["sh", "-s"],
            BuildRemoteBootstrapSnapshotScript(),
            secret,
            cancellationToken);
        EnsureRemoteSuccess(device, snapshot, "원격 부트스트랩 이전 상태 저장");

        var encoded = new string(
            snapshot.StandardOutput.Where(character => !char.IsWhiteSpace(character)).ToArray());
        if (encoded.Length > 96 * 1024 * 1024)
        {
            throw new CodexSyncBarException("원격 부트스트랩 복구 archive가 너무 큽니다.");
        }

        byte[] archive;
        try
        {
            archive = Convert.FromBase64String(encoded);
        }
        catch (FormatException error)
        {
            throw new CodexSyncBarException(
                "원격 부트스트랩 복구 archive를 해석하지 못했습니다.",
                error);
        }

        return _bootstrapTransactions.Begin(
            device.Id,
            EndpointFingerprint(device),
            archive);
    }

    private async Task RestoreRemoteBootstrapSnapshotAsync(
        SshDeviceConfiguration device,
        RemoteBootstrapTransaction transaction,
        string? secret,
        CancellationToken cancellationToken)
    {
        var archiveName = $".syncbar-bootstrap-restore-{Guid.NewGuid():N}.tar";
        var remoteArchive = $"~/{archiveName}";
        _ = _bootstrapTransactions.ReadArchive(transaction);
        try
        {
            await UploadFileAsync(
                device,
                _bootstrapTransactions.ArchivePath(transaction),
                remoteArchive,
                secret,
                cancellationToken);
            var restore = await RunSshWithInputAsync(
                device,
                ["sh", "-s"],
                BuildRemoteBootstrapRestoreScript(archiveName),
                secret,
                cancellationToken);
            EnsureRemoteSuccess(device, restore, "원격 부트스트랩 이전 상태 복구");
        }
        catch
        {
            try
            {
                await RunRemoteActionAsync(
                    device,
                    ["rm", "-f", remoteArchive],
                    secret,
                    CancellationToken.None);
            }
            catch
            {
                // Preserve both the local recovery journal and the original
                // restore failure. The next startup will retry the same archive.
            }

            throw;
        }

        try
        {
            await RunRemoteActionAsync(
                device,
                ["rm", "-f", remoteArchive],
                secret,
                CancellationToken.None);
        }
        catch (Exception error)
        {
            throw new CodexSyncBarException(
                "원격 부트스트랩 복구는 완료됐지만 임시 archive를 제거하지 못했습니다.",
                error);
        }
    }

    private static string EndpointFingerprint(SshDeviceConfiguration device)
    {
        var endpoint = string.Join(
            "\n",
            device.Host,
            device.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            device.Username,
            device.Authentication,
            device.IdentityFile ?? string.Empty,
            device.CertificateFile ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)))
            .ToLowerInvariant();
    }

    private static string BuildRemoteBootstrapSnapshotScript() =>
        """
        set -eu
        umask 077
        cd "$HOME"
        path_exists() {
          [ -e "$1" ] || [ -L "$1" ]
        }
        for directory in .local .local/bin .local/lib .local/lib/gpt-switch .local/share .codex; do
          if path_exists "$directory"; then
            [ -d "$directory" ] && [ ! -L "$directory" ]
          fi
        done
        for path in .local/bin/gpt-switch .local/lib/gpt-switch/codex-syncbar-askpass \
          .local/lib/gpt-switch/usage-summary.mjs .local/share/gpt-switch .codex/auth.json; do
          if path_exists "$path"; then
            [ ! -L "$path" ]
            case "$path" in
              .local/share/gpt-switch) [ -d "$path" ] ;;
              *) [ -f "$path" ] ;;
            esac
          fi
        done
        set --
        for path in .local/bin/gpt-switch .local/lib/gpt-switch/codex-syncbar-askpass \
          .local/lib/gpt-switch/usage-summary.mjs .local/share/gpt-switch .codex/auth.json; do
          if path_exists "$path"; then
            set -- "$@" "$path"
          fi
        done
        temporary=$(mktemp "$HOME/.syncbar-bootstrap-snapshot.XXXXXX")
        trap 'rm -f "$temporary"' EXIT HUP INT TERM
        if [ "$#" -eq 0 ]; then
          tar -cf "$temporary" -T /dev/null
        else
          tar -cf "$temporary" "$@"
        fi
        base64 "$temporary" | tr -d '\r\n'
        """;

    private static string BuildRemoteBootstrapRestoreScript(string archiveName)
    {
        if (!Regex.IsMatch(archiveName, "^\\.syncbar-bootstrap-restore-[a-f0-9]{32}\\.tar$"))
        {
            throw new CodexSyncBarException("원격 부트스트랩 임시 archive 이름이 올바르지 않습니다.");
        }

        return $$"""
        set -eu
        umask 077
        archive="$HOME/{{archiveName}}"
        path_exists() {
          [ -e "$1" ] || [ -L "$1" ]
        }
        owner_of() {
          stat -c "%u" "$1" 2>/dev/null || stat -f "%u" "$1"
        }
        validate_current() {
          path="$1"
          kind="$2"
          if path_exists "$path"; then
            [ ! -L "$path" ]
            if [ "$kind" = "file" ]; then
              [ -f "$path" ]
            else
              [ -d "$path" ]
            fi
            [ "$(owner_of "$path")" = "$(id -u)" ]
          fi
        }
        [ -f "$archive" ] && [ ! -L "$archive" ]
        chmod 600 "$archive"
        [ "$(owner_of "$archive")" = "$(id -u)" ]
        stage=$(mktemp -d "$HOME/.syncbar-bootstrap-stage.XXXXXX")
        old=$(mktemp -d "$HOME/.syncbar-bootstrap-old.XXXXXX")
        chmod 700 "$stage" "$old"
        mkdir "$stage/root"
        committed=0
        rollback_failed=0
        cleanup() {
          rc=$?
          trap - EXIT HUP INT TERM
          if [ "$committed" -ne 1 ]; then
            for item in auth state usage askpass helper; do
              if [ "$(cat "$old/$item.installed" 2>/dev/null || printf '0')" = "1" ]; then
                target=""
                kind="file"
                case "$item" in
                  auth) target="$HOME/.codex/auth.json" ;;
                  state) target="$HOME/.local/share/gpt-switch"; kind="directory" ;;
                  usage) target="$HOME/.local/lib/gpt-switch/usage-summary.mjs" ;;
                  askpass) target="$HOME/.local/lib/gpt-switch/codex-syncbar-askpass" ;;
                  helper) target="$HOME/.local/bin/gpt-switch" ;;
                esac
                if path_exists "$target"; then
                  if [ -L "$target" ]; then rollback_failed=1; else rm -rf "$target" || rollback_failed=1; fi
                fi
              fi
            done
            for item in auth state usage askpass helper; do
              if [ "$(cat "$old/$item.existed" 2>/dev/null || printf '0')" = "1" ]; then
                target=""
                case "$item" in
                  auth) target="$HOME/.codex/auth.json" ;;
                  state) target="$HOME/.local/share/gpt-switch" ;;
                  usage) target="$HOME/.local/lib/gpt-switch/usage-summary.mjs" ;;
                  askpass) target="$HOME/.local/lib/gpt-switch/codex-syncbar-askpass" ;;
                  helper) target="$HOME/.local/bin/gpt-switch" ;;
                esac
                if path_exists "$target"; then rm -rf "$target" || rollback_failed=1; fi
                mv "$old/$item" "$target" || rollback_failed=1
              fi
            done
          fi
          rm -rf "$stage"
          if [ "$committed" -eq 1 ]; then
            rm -rf "$old"
            rm -f "$archive"
          elif [ "$rollback_failed" -eq 1 ]; then
            printf '%s\n' "bootstrap rollback failed; old state preserved at $old" >&2
            rc=1
          else
            rm -rf "$old"
          fi
          exit "$rc"
        }
        trap cleanup EXIT HUP INT TERM
        tar -tf "$archive" >"$stage/entries"
        tar -tvf "$archive" >"$stage/verbose"
        while IFS= read -r entry || [ -n "$entry" ]; do
          case "$entry" in
            ""|/*|*..*|*//*|./*|*/./*|*[!A-Za-z0-9._/-]*) exit 1 ;;
          esac
          case "$entry" in
            .local/bin/gpt-switch|.local/lib/gpt-switch/codex-syncbar-askpass|\
            .local/lib/gpt-switch/usage-summary.mjs|.local/share/gpt-switch|\
            .local/share/gpt-switch/*|.codex/auth.json) ;;
            *) exit 1 ;;
          esac
        done <"$stage/entries"
        while IFS= read -r listing || [ -n "$listing" ]; do
          [ -n "$listing" ] || exit 1
          first=${listing%"${listing#?}"}
          case "$first" in d|-) ;; *) exit 1 ;; esac
        done <"$stage/verbose"
        [ "$(wc -l <"$stage/entries" | tr -d ' ')" = "$(wc -l <"$stage/verbose" | tr -d ' ')" ]
        [ -z "$(sort "$stage/entries" | uniq -d)" ]
        tar -xpf "$archive" -C "$stage/root"
        [ -z "$(find "$stage/root" -type l -print -quit)" ]
        for path in .local/bin/gpt-switch .local/lib/gpt-switch/codex-syncbar-askpass \
          .local/lib/gpt-switch/usage-summary.mjs .local/share/gpt-switch .codex/auth.json; do
          if path_exists "$stage/root/$path"; then
            [ ! -L "$stage/root/$path" ]
            [ -f "$stage/root/$path" ] || [ -d "$stage/root/$path" ]
          fi
        done
        ensure_parent() {
          parent="$1"
          if path_exists "$parent"; then
            [ -d "$parent" ] && [ ! -L "$parent" ]
          else
            mkdir "$parent"
          fi
        }
        ensure_parent "$HOME/.local"
        ensure_parent "$HOME/.local/bin"
        ensure_parent "$HOME/.local/lib"
        ensure_parent "$HOME/.local/lib/gpt-switch"
        ensure_parent "$HOME/.local/share"
        ensure_parent "$HOME/.codex"
        backup_item() {
          source="$1"
          name="$2"
          kind="$3"
          validate_current "$source" "$kind"
          if path_exists "$source"; then
            printf '1\n' >"$old/$name.existed"
            mv "$source" "$old/$name"
          else
            printf '0\n' >"$old/$name.existed"
          fi
        }
        install_item() {
          source="$stage/root/$1"
          target="$HOME/$1"
          name="$2"
          kind="$3"
          if path_exists "$source"; then
            validate_current "$source" "$kind"
            mv "$source" "$target"
            printf '1\n' >"$old/$name.installed"
          else
            printf '0\n' >"$old/$name.installed"
          fi
        }
        backup_item "$HOME/.codex/auth.json" auth file
        backup_item "$HOME/.local/share/gpt-switch" state directory
        backup_item "$HOME/.local/lib/gpt-switch/usage-summary.mjs" usage file
        backup_item "$HOME/.local/lib/gpt-switch/codex-syncbar-askpass" askpass file
        backup_item "$HOME/.local/bin/gpt-switch" helper file
        install_item .codex/auth.json auth file
        install_item .local/share/gpt-switch state directory
        install_item .local/lib/gpt-switch/usage-summary.mjs usage file
        install_item .local/lib/gpt-switch/codex-syncbar-askpass askpass file
        install_item .local/bin/gpt-switch helper file
        for item in auth state usage askpass helper; do
          if [ "$(cat "$old/$item.installed")" = "1" ]; then
            target=""
            kind="file"
            case "$item" in
              auth) target="$HOME/.codex/auth.json" ;;
              state) target="$HOME/.local/share/gpt-switch"; kind="directory" ;;
              usage) target="$HOME/.local/lib/gpt-switch/usage-summary.mjs" ;;
              askpass) target="$HOME/.local/lib/gpt-switch/codex-syncbar-askpass" ;;
              helper) target="$HOME/.local/bin/gpt-switch" ;;
            esac
            validate_current "$target" "$kind"
          fi
        done
        committed=1
        printf '%s\n' 'remote bootstrap state restored'
        """;
    }

    private async Task InstallRemoteHelpersAsync(
        SshDeviceConfiguration device,
        string? secret,
        CancellationToken cancellationToken)
    {
        await InstallRemoteBundleAsync(
            device,
            secret,
            cancellationToken,
            [
                new(_paths.BundledGptSwitch, "gpt-switch", ".local/bin/gpt-switch", "755"),
                new(
                    _paths.BundledAskPass,
                    "codex-syncbar-askpass",
                    ".local/lib/gpt-switch/codex-syncbar-askpass",
                    "700"),
                new(
                    _paths.BundledUsageSummary,
                    "usage-summary.mjs",
                    ".local/lib/gpt-switch/usage-summary.mjs",
                    "755"),
            ],
            "원격 helper 설치");
    }

    private async Task InstallRemoteCursorHelpersAsync(
        SshDeviceConfiguration device,
        string? secret,
        CancellationToken cancellationToken)
    {
        await InstallRemoteBundleAsync(
            device,
            secret,
            cancellationToken,
            [
                new(
                    _paths.BundledCursorBridge,
                    "cursor-codex-bridge.mjs",
                    ".local/lib/gpt-switch/cursor-codex-bridge.mjs",
                    "755"),
                new(
                    _paths.BundledCursorRemoteManager,
                    "cursor-remote-manager.mjs",
                    ".local/lib/gpt-switch/cursor-remote-manager.mjs",
                    "755"),
            ],
            "원격 Cursor helper 설치");
    }

    private async Task InstallRemoteBundleAsync(
        SshDeviceConfiguration device,
        string? secret,
        CancellationToken cancellationToken,
        IReadOnlyList<RemoteBundleFile> files,
        string operation)
    {
        foreach (var file in files)
        {
            WindowsPathSafety.EnsureFile(file.LocalPath, $"{operation} 파일");
            if (!File.Exists(file.LocalPath))
            {
                throw new CodexSyncBarException(
                    $"{operation} 파일이 앱 패키지에 포함되어 있지 않습니다. Windows 앱을 다시 빌드하거나 설치해 주세요.");
            }
        }

        var stageRelativePath = $".local/share/gpt-switch/.syncbar-install-{Guid.NewGuid():N}";
        var stagePath = $"~/{stageRelativePath}";
        var prepare = await RunSshWithInputAsync(
            device,
            ["sh", "-s"],
            $$"""
            set -eu
            umask 077
            state="$HOME/.local/share/gpt-switch"
            stage="$HOME/{{stageRelativePath}}"
            mkdir -p "$HOME/.local/bin" "$HOME/.local/lib/gpt-switch" "$state/profiles"
            for directory in "$HOME/.local/bin" "$HOME/.local/lib/gpt-switch" "$state" "$state/profiles"; do
              [ -d "$directory" ] && [ ! -L "$directory" ]
            done
            if [ -e "$stage" ] || [ -L "$stage" ]; then
              exit 1
            fi
            mkdir "$stage"
            chmod 700 "$stage"
            """,
            secret,
            cancellationToken);
        EnsureRemoteSuccess(device, prepare, $"{operation} 디렉터리 준비");

        try
        {
            foreach (var file in files)
            {
                await UploadFileAsync(
                    device,
                    file.LocalPath,
                    $"{stagePath}/{file.StageName}",
                    secret,
                    cancellationToken);
            }

            var commit = await RunSshWithInputAsync(
                device,
                ["sh", "-s"],
                BuildRemoteBundleInstallScript(stageRelativePath, files),
                secret,
                cancellationToken);
            EnsureRemoteSuccess(device, commit, operation);
        }
        catch
        {
            try
            {
                await RunRemoteActionAsync(
                    device,
                    ["rm", "-rf", stagePath],
                    secret,
                    CancellationToken.None);
            }
            catch
            {
                // Preserve the original failure. The remote transaction script
                // keeps its backup when rollback itself cannot complete.
            }

            throw;
        }
    }

    private static string BuildRemoteBundleInstallScript(
        string stageRelativePath,
        IReadOnlyList<RemoteBundleFile> files)
    {
        var script = new StringBuilder($$"""
            set -eu
            umask 077
            state="$HOME/.local/share/gpt-switch"
            stage="$HOME/{{stageRelativePath}}"
            transaction=$(mktemp -d "$state/.syncbar-install.XXXXXX")
            chmod 700 "$transaction"
            committed=0
            rollback_failed=0
            new_file=""
            mode_of() {
              stat -c "%a" "$1" 2>/dev/null || stat -f "%Lp" "$1"
            }
            owner_of() {
              stat -c "%u" "$1" 2>/dev/null || stat -f "%u" "$1"
            }
            validate_destination() {
              destination="$1"
              if [ -e "$destination" ] || [ -L "$destination" ]; then
                [ -f "$destination" ] && [ ! -L "$destination" ]
                [ "$(owner_of "$destination")" = "$(id -u)" ]
              fi
            }
            backup_one() {
              destination="$1"
              backup="$2"
              validate_destination "$destination"
              if [ -e "$destination" ] || [ -L "$destination" ]; then
                printf '1\n' >"$backup.existed"
                mode_of "$destination" >"$backup.mode"
                cp "$destination" "$backup.file"
              else
                printf '0\n' >"$backup.existed"
              fi
            }
            install_one() {
              source="$1"
              destination="$2"
              mode="$3"
              backup="$4"
              new_file=$(mktemp "$(dirname "$destination")/.syncbar-new.XXXXXX")
              cp "$source" "$new_file"
              chmod "$mode" "$new_file"
              [ -f "$new_file" ] && [ ! -L "$new_file" ]
              [ "$(owner_of "$new_file")" = "$(id -u)" ]
              [ "$(mode_of "$new_file")" = "$mode" ]
              printf '1\n' >"$backup.installed"
              mv -f "$new_file" "$destination"
              new_file=""
            }
            restore_one() {
              destination="$1"
              backup="$2"
              if [ "$(cat "$backup.installed" 2>/dev/null || printf '0')" != "1" ]; then
                return 0
              fi
              if [ "$(cat "$backup.existed")" = "1" ]; then
                new_file=$(mktemp "$(dirname "$destination")/.syncbar-rollback.XXXXXX")
                cp "$backup.file" "$new_file"
                chmod "$(cat "$backup.mode")" "$new_file"
                mv -f "$new_file" "$destination"
              else
                rm -f "$destination"
              fi
            }
            cleanup() {
              rc=$?
              trap - EXIT HUP INT TERM
              if [ "$committed" -ne 1 ]; then
            """);
        for (var index = files.Count - 1; index >= 0; index--)
        {
            var file = files[index];
            script.AppendLine(
                $"  restore_one \"$HOME/{file.DestinationRelativePath}\" \"$transaction/item-{index}\" || rollback_failed=1");
        }

        script.AppendLine(
            "  fi");
        script.AppendLine(
            "  [ -z \"${new_file:-}\" ] || rm -f \"$new_file\"");
        script.AppendLine(
            "  if [ \"$rollback_failed\" -eq 0 ]; then");
        script.AppendLine(
            "    rm -rf \"$transaction\"");
        script.AppendLine(
            "    if [ -d \"$stage\" ] && [ ! -L \"$stage\" ]; then rm -rf \"$stage\"; fi");
        script.AppendLine(
            "  else");
        script.AppendLine(
            "    printf '%s\\n' \"remote install rollback failed; backup preserved at $transaction\" >&2");
        script.AppendLine(
            "  fi");
        script.AppendLine(
            "  exit \"$rc\"");
        script.AppendLine(
            "}");
        script.AppendLine(
            "trap cleanup EXIT HUP INT TERM");
        script.AppendLine(
            "[ -d \"$stage\" ] && [ ! -L \"$stage\" ]");

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            script.AppendLine(
                $"[ -f \"$stage/{file.StageName}\" ] && [ ! -L \"$stage/{file.StageName}\" ]");
            script.AppendLine(
                $"backup_one \"$HOME/{file.DestinationRelativePath}\" \"$transaction/item-{index}\"");
        }

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            script.AppendLine(
                $"install_one \"$stage/{file.StageName}\" \"$HOME/{file.DestinationRelativePath}\" \"{file.Mode}\" \"$transaction/item-{index}\"");
        }

        script.AppendLine("committed=1");
        script.AppendLine("rm -rf \"$transaction\" \"$stage\"");
        script.AppendLine("trap - EXIT HUP INT TERM");
        script.AppendLine("printf '%s\\n' 'remote bundle installed'");
        return script.ToString();
    }

    private async Task UploadFileAsync(
        SshDeviceConfiguration device,
        string localFile,
        string remoteFile,
        string? secret,
        CancellationToken cancellationToken)
    {
        var scp = FindExecutable("scp.exe", "scp");
        var arguments = BuildCommonOptions(device, forScp: true, secret is not null);
        arguments.Add(localFile);
        arguments.Add($"{device.Username}@{device.Host}:{remoteFile}");
        var result = await ProcessRunner.RunAsync(
            scp,
            arguments,
            cancellationToken: cancellationToken,
            timeout: TimeSpan.FromSeconds(60),
            environment: AskPassEnvironment(device, secret)?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value));
        if (result.ExitCode != 0)
        {
            throw new CodexSyncBarException(
                $"{device.DisplayLabel}에 파일을 전송하지 못했습니다: {result.CombinedOutput.Trim()}");
        }
    }

    private async Task<ProcessResult> RunRemoteActionAsync(
        SshDeviceConfiguration device,
        IEnumerable<string> arguments,
        string? secret,
        CancellationToken cancellationToken)
    {
        return await RunSshAsync(
            device,
            arguments,
            cancellationToken,
            TimeSpan.FromSeconds(60),
            secret);
    }

    private async Task<ProcessResult> RunSshWithInputAsync(
        SshDeviceConfiguration device,
        IEnumerable<string> remoteArguments,
        string standardInput,
        string? secret,
        CancellationToken cancellationToken)
    {
        var ssh = FindExecutable("ssh.exe", "ssh");
        var arguments = BuildCommonOptions(device, forScp: false, secret is not null);
        arguments.Add($"{device.Username}@{device.Host}");
        arguments.Add(string.Join(' ', remoteArguments));
        return await ProcessRunner.RunAsync(
            ssh,
            arguments,
            standardInput: standardInput,
            cancellationToken: cancellationToken,
            timeout: TimeSpan.FromSeconds(120),
            environment: AskPassEnvironment(device, secret)?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value));
    }

    private static void EnsureRemoteSuccess(
        SshDeviceConfiguration device,
        ProcessResult result,
        string operation)
    {
        if (result.ExitCode != 0)
        {
            throw new CodexSyncBarException(
                $"{device.DisplayLabel} {operation}에 실패했습니다: {result.CombinedOutput.Trim()}");
        }
    }

    private static IReadOnlyDictionary<string, string> ParseFields(string output) =>
        output
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last()[1],
                StringComparer.Ordinal);

    private static string TrimRemoteOutput(ProcessResult result)
    {
        var message = result.CombinedOutput.Trim();
        return message.Length <= 1_024 ? message : message[..1_024];
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..12];

    public async Task<DeviceTokenUsageSummary> FetchTokenUsageAsync(
        SshDeviceConfiguration device,
        CancellationToken cancellationToken = default)
    {
        var secret = ResolveSecret(device);
        await EnsureRemoteHelpersAsync(device, secret, cancellationToken);
        var result = await RunSshAsync(
            device,
            [
                "~/.local/bin/gpt-switch",
                "__node",
                "usage-summary",
            ],
            cancellationToken,
            TimeSpan.FromSeconds(60),
            secret);
        if (result.ExitCode != 0)
        {
            throw new CodexSyncBarException(
                $"{device.DisplayLabel}의 토큰 사용량을 가져오지 못했습니다: {result.CombinedOutput.Trim()}");
        }

        return TokenUsageService.ParseSummary(result.StandardOutput);
    }

    private async Task EnsureRemoteHelpersAsync(
        SshDeviceConfiguration device,
        string? secret,
        CancellationToken cancellationToken)
    {
        var version = await RunRemoteActionAsync(
            device,
            ["~/.local/bin/gpt-switch", "__node", "version"],
            secret,
            cancellationToken);
        if (version.ExitCode == 0
            && string.Equals(version.StandardOutput.Trim(), ExpectedHelperVersion, StringComparison.Ordinal))
        {
            return;
        }

        await InstallRemoteHelpersAsync(device, secret, cancellationToken);
    }

    private async Task<ProcessResult> RunSshAsync(
        SshDeviceConfiguration device,
        IEnumerable<string> remoteArguments,
        CancellationToken cancellationToken,
        TimeSpan timeout,
        string? secret = null)
    {
        var ssh = FindExecutable("ssh.exe", "ssh");
        var arguments = BuildCommonOptions(device, forScp: false, secret is not null);
        arguments.Add($"{device.Username}@{device.Host}");
        arguments.Add(string.Join(' ', remoteArguments));
        return await ProcessRunner.RunAsync(
            ssh,
            arguments,
            cancellationToken: cancellationToken,
            timeout: timeout,
            environment: AskPassEnvironment(device, secret)?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value));
    }

    private static List<string> BuildCommonOptions(
        SshDeviceConfiguration device,
        bool forScp,
        bool hasAskPassSecret)
    {
        var arguments = new List<string>
        {
            "-o", hasAskPassSecret ? "BatchMode=no" : "BatchMode=yes",
            "-o", "ConnectTimeout=8",
            "-o", "StrictHostKeyChecking=yes",
            "-o", "ForwardAgent=no",
            "-o", "ForwardX11=no",
            "-o", "RequestTTY=no",
            "-o", "ClearAllForwardings=yes",
            forScp ? "-P" : "-p",
            device.Port.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(device.IdentityFile))
        {
            arguments.Add("-i");
            arguments.Add(device.IdentityFile!);
        }

        if (!string.IsNullOrWhiteSpace(device.CertificateFile))
        {
            arguments.Add("-o");
            arguments.Add($"CertificateFile={device.CertificateFile}");
        }

        if (hasAskPassSecret && device.Authentication == "password")
        {
            arguments.AddRange(
            [
                "-o", "PubkeyAuthentication=no",
                "-o", "PreferredAuthentications=password",
                "-o", "KbdInteractiveAuthentication=no",
                "-o", "NumberOfPasswordPrompts=1",
            ]);
        }

        return arguments;
    }

    private string? ResolveSecret(SshDeviceConfiguration device)
    {
        if (device.CredentialId is not { } credentialId)
        {
            if (device.Authentication == "password" || device.HasKeyPassphrase)
            {
                throw new CodexSyncBarException("SSH 비밀 저장소 식별자가 없습니다.");
            }

            return null;
        }

        var key = credentialId.ToString("D");
        var secret = device.Authentication == "password"
            ? _secretStore.Read($"{key}.password")
            : device.HasKeyPassphrase
                ? _secretStore.Read($"{key}.passphrase")
                : null;
        if ((device.Authentication == "password" || device.HasKeyPassphrase)
            && string.IsNullOrEmpty(secret))
        {
            throw new CodexSyncBarException(
                device.Authentication == "password"
                    ? "SSH 비밀번호를 먼저 저장해 주세요."
                    : "SSH 키 암호를 먼저 저장해 주세요.");
        }

        return secret;
    }

    private IDictionary<string, string?>? AskPassEnvironment(
        SshDeviceConfiguration device,
        string? secret)
    {
        if (secret is null)
        {
            return null;
        }

        EnsureAskPassScript();
        return new Dictionary<string, string?>
        {
            ["SSH_ASKPASS"] = _paths.SshAskPassFile,
            ["SSH_ASKPASS_REQUIRE"] = "force",
            ["DISPLAY"] = "codex-syncbar",
            ["CODEX_SYNCBAR_ASKPASS_SECRET"] = secret,
        };
    }

    private void EnsureAskPassScript()
    {
        _paths.EnsureDirectories();
        WindowsPathSafety.EnsureFile(_paths.SshAskPassFile, "SSH askpass helper");
        if (File.Exists(_paths.SshAskPassFile))
        {
            WindowsPathSafety.EnsurePrivateFile(
                _paths.SshAskPassFile,
                "SSH askpass helper",
                64 * 1024);
            return;
        }

        var directory = Path.GetDirectoryName(_paths.SshAskPassFile)!;
        var temporary = Path.Combine(directory, $".ssh-askpass.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(
            temporary,
            "@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"[Console]::Write($env:CODEX_SYNCBAR_ASKPASS_SECRET)\"\r\n",
            new System.Text.UTF8Encoding(false));
        try
        {
            File.Move(temporary, _paths.SshAskPassFile, overwrite: false);
        }
        catch (IOException) when (File.Exists(_paths.SshAskPassFile))
        {
            // Another SyncBar process won the helper creation race.
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        WindowsPathSafety.EnsurePrivateFile(
            _paths.SshAskPassFile,
            "SSH askpass helper",
            64 * 1024);
    }

    private static string FindExecutable(params string[] names)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var name in names)
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new CodexSyncBarException("Windows OpenSSH(ssh/scp)를 찾지 못했습니다.");
    }
}
