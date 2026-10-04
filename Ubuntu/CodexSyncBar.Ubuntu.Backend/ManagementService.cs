using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Ubuntu.Backend;

public sealed class ManagementService : IManagementService
{
    private readonly WindowsPaths _paths;
    private readonly ConfigurationStore _config;
    private readonly AuthStore _auth;
    private readonly LocalSwitchService _local;
    private readonly SshDeviceService _ssh;
    private readonly BrowserLoginService _browser;
    private readonly SyncBarController _controller;
    private readonly object _state = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, PreviewEntry> _previews = [];
    private readonly Dictionary<string, (string PreviewId, OperationView Operation)> _appliedRequests = [];
    private readonly Dictionary<string, OperationView> _history = [];
    private IReadOnlyList<DeviceView> _devices = [];
    private OperationView? _operation;
    private bool _busy;
    private string? _error;
    private long _deviceGeneration;
    private long _deviceRequest;
    private CancellationTokenSource? _loginCancellation;
    private readonly Task _initialization;
    private Task? _job;
    private bool _shutdownRequested;
    public bool ShutdownRequested { get { lock (_state) return _shutdownRequested; } }

    private sealed record PreviewEntry(SwitchPreview View, string Observations, string TargetAccountId);

    public ManagementService(WindowsPaths paths, UsageService? usageService = null)
    {
        _paths = paths;
        _config = new(paths);
        _auth = new(paths);
        _local = new(_auth, paths);
        _ssh = new(_auth, paths, _local);
        _browser = new(paths);
        _controller = new(paths, usageService);
        _initialization = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _controller.InitializeAsync(_lifetime.Token);
            var recovered = await _controller.GetSnapshotAsync(_lifetime.Token);
            if (recovered.Operation?.State == "recoveryRequired")
                lock (_state)
                {
                    _operation = FromSwitch(recovered.Operation, "recovery");
                    _error = "이전 작업의 복구를 먼저 완료해 주세요.";
                }
            await RefreshDevicesAsync(_lifetime.Token);
        }
        catch (Exception) when (!_lifetime.IsCancellationRequested)
        {
            lock (_state) _error = "시작 복구를 완료하지 못했습니다. 비밀 저장소와 장치 연결을 확인한 뒤 복구를 다시 시도해 주세요.";
        }
    }

    public async Task<object?> DispatchAsync(string method, JsonElement p, CancellationToken token)
    {
        await _initialization.WaitAsync(token);
        lock (_state)
            if (method is not ("snapshot" or "operation" or "quit" or "account.cancel")) ThrowIfStopping();
        switch (method)
        {
            case "snapshot": return await SnapshotAsync(token);
            case "account.identityHashes": return await IdentityHashesAsync(token);
            case "preview": return await PreviewAsync(Wire.Number(p, "profileId"), token,
                Wire.Text(p, "deviceId") is { Length: > 0 } selectedDevice ? selectedDevice : null);
            case "apply": return Apply(Wire.Text(p, "previewId"), Wire.Text(p, "requestId"));
            case "operation":
                lock (_state) return _history.GetValueOrDefault(Wire.Text(p, "operationId")) ?? _operation;
            case "refresh": return StartJob("refresh", null, async ct =>
            {
                var kind = Wire.Text(p, "kind", "all");
                if (kind is "usage" or "all") await _controller.RefreshUsageAsync(ct);
                if (kind is "devices" or "all") await RefreshDevicesAsync(ct, allowBusy: true);
                return kind == "tokens" ? await TokensAsync(ct) : null;
            });
            case "tokens": return await TokensAsync(token);
            case "account.add": return StartLogin(null, false, false);
            case "account.login": return StartLogin(Wire.Number(p, "profileId"), Wire.Flag(p, "replaceExisting"), Wire.Flag(p, "fresh"));
            case "account.cancel": _loginCancellation?.Cancel(); return new { cancelled = true };
            case "account.reopen": _browser.ReopenLogin(Wire.Number(p, "profileId")); return new { opened = true };
            case "account.import": return StartJob("import", OptionalProfile(p), ct => ImportAsync(p, ct));
            case "account.rename": return await MutateAsync(() =>
            {
                var c = _config.LoadOrCreate();
                _config.UpdateAccountAlias(c, RequiredAccount(c, p).Id, Wire.Text(p, "alias"));
                return new { saved = true };
            }, token);
            case "account.move": return await MutateAsync(() =>
            {
                var c = _config.LoadOrCreate();
                var account = RequiredAccount(c, p);
                var ids = c.Accounts.Select(a => a.Id).ToList();
                var index = ids.IndexOf(account.Id);
                var next = Math.Clamp(index + Wire.Number(p, "offset"), 0, ids.Count - 1);
                (ids[index], ids[next]) = (ids[next], ids[index]);
                _config.ReorderAccounts(c, ids);
                return new { saved = true };
            }, token);
            case "account.delete": return await MutateAsync(() =>
            {
                var c = _config.LoadOrCreate();
                var account = RequiredAccount(c, p);
                if (_auth.ProfileArtifactExists(account.Id)) throw new RpcException("logout_required", "먼저 계정을 로그아웃해 주세요.");
                try { _browser.ClearProfile(account.Id); } catch { _browser.MarkCleanupPending(account.Id); }
                _config.RemoveAccount(c, account.Id);
                return new { removed = true };
            }, token);
            case "account.logout": return StartJob("logout", Wire.Number(p, "profileId"), async ct =>
            {
                var result = await _controller.LogoutAccountAsync(Wire.Number(p, "profileId"), Wire.Number(p, "fallbackProfileId"), ct);
                SetOperation(FromSwitch(result, "logout"));
                await RefreshDevicesAsync(ct, allowBusy: true);
                return null;
            });
            case "auth.refresh": return StartJob("auth-refresh", OptionalProfile(p), ct => MaintainAuthAsync(OptionalProfile(p), true, ct));
            case "auth.sync": return StartJob("auth-sync", OptionalProfile(p), ct => MaintainAuthAsync(OptionalProfile(p), false, ct));
            case "recovery.retry": return StartJob("recovery", null, async ct =>
            {
                await _controller.RetryRecoveryAsync(ct);
                var s = await _controller.GetSnapshotAsync(ct);
                if (s.Operation?.State == "recoveryRequired") SetOperation(FromSwitch(s.Operation, "recovery"));
                else lock (_state) _error = null;
                await RefreshDevicesAsync(ct, allowBusy: true);
                return _controller.RecoveryStatus;
            }, allowRecovery: true);
            case "device.list": return _config.LoadOrCreate().Devices;
            case "device.save": return await MutateAsync(() => SaveDevice(p), token);
            case "device.delete": return await MutateAsync(() => DeleteDevice(p), token);
            case "device.test":
            {
                var device = RequiredDevice(p);
                var key = await new SshHostTrust().InspectAsync(device, token);
                if (key is not null) return new { isReachable = false, message = "서버 지문 확인이 필요합니다.", hostKey = key };
                var tested = await _ssh.TestConnectionAsync(device, token);
                return new { tested.IsReachable, message = tested.IsReachable ? "SSH 연결이 정상입니다." : "SSH 연결 또는 인증을 확인해 주세요.", hostKey = (SshHostKey?)null };
            }
            case "device.trust": return await TrustDeviceAsync(p, token);
            case "device.bootstrap":
            {
                var device = RequiredDevice(p);
                var key = await new SshHostTrust().InspectAsync(device, token);
                if (key is not null) throw new RpcException("trust_required", "서버 지문을 먼저 확인해 주세요.", new { hostKey = key });
                return StartJob("bootstrap", null, ct => BootstrapAsync(device.Id, ct));
            }
            case "device.apply":
            {
                var preview = await PreviewAsync(Wire.Number(p, "profileId"), token, Wire.Text(p, "deviceId"));
                return Apply(preview.PreviewId, Wire.Text(p, "requestId", Guid.NewGuid().ToString("N")));
            }
            case "cli.status": return await CliService(Wire.Text(p, "deviceId", "local")).InspectAsync(token);
            case "cli.update": return StartJob("cli-update", null, async ct =>
            {
                using var lease = await ControllerMutationLock.AcquireAsync(_paths, cancellationToken: ct);
                var result = await CliService(Wire.Text(p, "deviceId", "local")).UpdateAsync(ct);
                CompleteCurrent(result.DisplayText, result);
                return result;
            });
            case "cli.updateAll": return StartJob("cli-update-all", null, UpdateAllCliAsync);
            case "settings.get": return Settings();
            case "settings.save": return await MutateAsync(() => SaveSettings(p), token);
            case "weekly.set": return StartJob("weekly-settings", Wire.Number(p, "profileId"), async _ =>
            {
                await _controller.SetWeeklyAnchorEnabledAsync(Wire.Number(p, "profileId"), Wire.Flag(p, "enabled"));
                return Settings();
            });
            case "weekly.send": return StartJob("weekly-message", Wire.Number(p, "profileId"), async _ =>
            {
                var sent = await _controller.SendWeeklyAnchorNowAsync(Wire.Number(p, "profileId"));
                if (!sent) throw new RpcException("weekly_not_sent", "주간 메시지를 보내지 못했습니다. 최신 주간 사용량과 진행 중인 작업을 확인해 주세요.");
                CompleteCurrent("주간 메시지를 보냈습니다.", new { sent = true });
                return new { sent = true };
            });
            case "open.codex": _local.OpenCodex(); return new { opened = true };
            case "open.codexFolder": _local.OpenCodexHome(); return new { opened = true };
            case "open.authFolder": _browser.OpenAuthFileFolder(); return new { opened = true };
            case "quit":
                lock (_state)
                {
                    _shutdownRequested = true;
                }
                return new { shutdownRequested = true };
            default: throw new RpcException("unknown_method", "지원하지 않는 작업입니다.");
        }
    }

    private async Task<ManagementSnapshot> SnapshotAsync(CancellationToken token)
    {
        var c = _config.LoadOrCreate();
        DashboardSnapshot dashboard;
        try { dashboard = await _controller.GetSnapshotAsync(token); }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            dashboard = new DashboardSnapshot { Error = "비밀 저장소 또는 계정 상태를 확인하지 못했습니다. 잠금 해제 후 복구를 다시 시도해 주세요." };
        }
        var accounts = c.Accounts.Select(a => new AccountView(a.Id, a.Email,
            a.CustomAlias ?? a.Email, a.IsPending, a.NeedsLogin,
            dashboard.Accounts.FirstOrDefault(x => x.ProfileId == a.Id)?.Usage)).ToArray();
        int? active = null;
        try { active = _local.GetActiveProfileId(c.Accounts); } catch (Exception) when (!token.IsCancellationRequested) { }
        lock (_state)
        {
            var operation = _operation ?? (dashboard.Operation is null ? null : FromSwitch(dashboard.Operation, "recovery"));
            return new(1, accounts, _devices, active, _busy, operation,
                Revision(c), DateTimeOffset.UtcNow, _error ?? dashboard.Error, Settings(),
                accounts.ToDictionary(a => a.Id.ToString(), a => a.Usage));
        }
    }

    private async Task<IReadOnlyList<DeviceView>> QueryDevicesAsync(AppConfiguration c, CancellationToken token)
    {
        var active = _local.GetActiveProfileId(c.Accounts);
        var observed = DateTimeOffset.UtcNow;
        var statuses = await _ssh.FetchStatusesAsync(c, active, token);
        _local.RefreshClientStatus();
        var identities = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var account in c.Accounts.Where(a => !a.IsPending && !a.NeedsLogin))
        {
            try { identities[Fingerprint(_auth.ReadCredentials(account.Id).AccountId)] = account.Id; }
            catch (AuthenticationRequiredException) { }
        }
        var rows = new List<DeviceView>
        {
            new("local", "이 Ubuntu PC", "local", true, true, active, AccountLabel(c, active), observed,
                active is null ? "unknown" : "ready", _local.ReconnectionDetail, _local.ReconnectionDetail is not null),
        };
        foreach (var device in c.Devices)
        {
            var status = statuses.FirstOrDefault(s => s.Id == device.Id);
            int? known = status?.AccountId is { } fingerprint && identities.TryGetValue(fingerprint, out var id) ? id : null;
            var mappingMismatch = known is not null && status?.ProfileId != known;
            rows.Add(new("ssh:" + device.Id, device.DisplayLabel, "ssh", device.Enabled,
                device.Enabled && status?.IsReachable == true, known, AccountLabel(c, known), observed,
                !device.Enabled ? "disabled" : status?.IsReachable != true ? "unreachable" : known is null ? "unknown" : mappingMismatch ? "mappingMismatch" : "ready",
                !device.Enabled ? "대상 제외 · 설치 및 활성화 필요" : status?.IsReachable != true ? "연결을 확인하지 못했습니다." : known is null ? "등록된 계정과 일치하지 않습니다." : mappingMismatch ? "계정 슬롯이 달라 설치 및 활성화가 필요합니다." : null));
        }
        return rows;
    }

    private async Task RefreshDevicesAsync(CancellationToken token, bool allowBusy = false)
    {
        long generation, request;
        lock (_state)
        {
            if (_busy && !allowBusy) return;
            generation = _deviceGeneration;
            request = ++_deviceRequest;
        }
        var c = _config.LoadOrCreate();
        var revision = Revision(c);
        var devices = await QueryDevicesAsync(c, token);
        if (Revision(_config.LoadOrCreate()) != revision) return;
        lock (_state)
            if (generation == _deviceGeneration && request == _deviceRequest) _devices = devices;
    }

    private async Task<SwitchPreview> PreviewAsync(int profileId, CancellationToken token, string? singleDevice = null)
    {
        lock (_state) if (_busy) throw new RpcException("busy", "진행 중인 작업을 먼저 완료해 주세요.");
        var c = _config.LoadOrCreate();
        var account = c.Accounts.FirstOrDefault(a => a.Id == profileId && !a.IsPending && !a.NeedsLogin)
            ?? throw new RpcException("account_unavailable", "로그인된 계정을 선택해 주세요.");
        var credential = _auth.ReadCredentials(profileId);
        var revision = Revision(c);
        var devices = await QueryDevicesAsync(c, token);
        if (Revision(_config.LoadOrCreate()) != revision) throw new RpcException("stale_preview", "설정이 변경되었습니다. 다시 선택해 주세요.");
        if (singleDevice is not null && !devices.Any(d => d.Id == NormalizeDeviceId(singleDevice) && d.Enabled))
            throw new RpcException("device_unavailable", "활성 장치를 선택해 주세요.");
        var targets = devices.Select(d => new PreviewTarget(d, profileId, account.CustomAlias ?? account.Email,
            !d.Enabled || singleDevice is not null && d.Id != NormalizeDeviceId(singleDevice) ? "excluded" : d.CurrentProfileId == profileId ? "reapply" : "switch",
            d.Enabled && (singleDevice is null || d.Id == NormalizeDeviceId(singleDevice)))).ToArray();
        bool recoveryRequired;
        lock (_state) recoveryRequired = _operation?.State == "recoveryRequired" || _error is not null;
        var canApply = !recoveryRequired && targets.Where(d => d.Included).All(d => d.IsReachable && (d.Kind == "local" || d.Status == "ready"));
        var view = new SwitchPreview(Guid.NewGuid().ToString("N"), profileId, revision, DateTimeOffset.UtcNow,
            canApply, canApply ? null : recoveryRequired ? "이전 작업의 복구를 먼저 완료해 주세요." : "모든 활성 장치의 연결과 현재 계정을 먼저 확인해 주세요.", targets);
        lock (_state)
        {
            foreach (var id in _previews.Where(x => DateTimeOffset.UtcNow - x.Value.View.CreatedAt > TimeSpan.FromMinutes(5)).Select(x => x.Key).ToArray()) _previews.Remove(id);
            if (_previews.Count >= 128) _previews.Remove(_previews.Keys.First());
            _previews[view.PreviewId] = new(view, ObservationKey(targets.Where(t => t.Included).Select(t => t.Device)), credential.AccountId);
            _devices = devices;
        }
        return view;
    }

    private OperationView Apply(string previewId, string requestId)
    {
        if (requestId.Length is < 1 or > 128) throw new RpcException("invalid_request", "요청 식별자가 필요합니다.");
        lock (_state)
        {
            if (_appliedRequests.TryGetValue(requestId, out var prior))
            {
                if (prior.PreviewId != previewId) throw new RpcException("duplicate_request", "같은 요청 식별자를 다른 전환에 사용할 수 없습니다.");
                return _history.GetValueOrDefault(prior.Operation.Id) ?? prior.Operation;
            }
            if (!_previews.TryGetValue(previewId, out var preview) || DateTimeOffset.UtcNow - preview.View.CreatedAt > TimeSpan.FromMinutes(5))
                throw new RpcException("stale_preview", "적용할 장치 상태를 다시 확인해 주세요.");
            if (!preview.View.CanApply) throw new RpcException("devices_unavailable", preview.View.BlockingReason!);
            var operation = StartJob("switch", preview.View.ProfileId, ct => ApplyCoreAsync(preview, ct));
            _appliedRequests[requestId] = (previewId, operation);
            return operation;
        }
    }

    private async Task<object?> ApplyCoreAsync(PreviewEntry preview, CancellationToken token)
    {
        using var lease = await ControllerMutationLock.AcquireAsync(_paths, TimeSpan.FromSeconds(30), token);
        var c = _config.LoadOrCreate();
        if (Revision(c) != preview.View.ConfigurationRevision || _auth.ReadCredentials(preview.View.ProfileId).AccountId != preview.TargetAccountId)
            throw new RpcException("stale_preview", "계정 또는 장치 설정이 변경되었습니다. 미리보기를 다시 확인해 주세요.");
        var current = await QueryDevicesAsync(c, token);
        var included = preview.View.Targets.Where(t => t.Included).Select(t => t.Id).ToHashSet();
        if (ObservationKey(current.Where(d => included.Contains(d.Id))) != preview.Observations)
            throw new RpcException("stale_preview", "장치의 현재 계정 또는 연결 상태가 변경되었습니다. 다시 확인해 주세요.");
        var targets = c.Devices.Where(d => included.Contains("ssh:" + d.Id) && d.Enabled).Select(_ssh.CreateAccountTarget).ToList();
        if (included.Contains("local")) targets.Add(new WindowsAccountTarget(_auth, _local, _paths));
        var coordinator = new AccountSwitchCoordinator(_paths);
        coordinator.Progress += o => SetOperation(FromSwitch(o, "switch"));
        string operationId;
        lock (_state) operationId = _operation!.Id;
        var result = await coordinator.SwitchAsync(preview.View.ProfileId, targets, operationId, token);
        SetOperation(FromSwitch(result, "switch"));
        await RefreshDevicesAsync(token, allowBusy: true);
        return null;
    }

    private OperationView StartLogin(int? profileId, bool replace, bool fresh) => StartJob("login", profileId, async ct =>
    {
        using var lease = await ControllerMutationLock.AcquireAsync(_paths, cancellationToken: ct);
        var c = _config.LoadOrCreate();
        var account = profileId is null ? _config.ReserveAccount(c) : c.Accounts.FirstOrDefault(a => a.Id == profileId)
            ?? throw new RpcException("account_unavailable", "계정 항목을 찾지 못했습니다.");
        lock (_state) if (_operation is { } operation) SetOperation(operation with { ProfileId = account.Id });
        if (fresh) _browser.ResetProfileForLogin(account.Id);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loginCancellation = cancel;
        try
        {
            await new CodexLoginService(_paths, _auth, _browser).LoginAsync(account.Id, replace,
                new Progress<string>(m => SetMessage(m)), cancel.Token);
            _config.UpdateAccountEmail(c, account.Id, _auth.ReadCredentials(account.Id).Email);
            var failed = await _ssh.SyncProfileAsync(c, account.Id, ct);
            var result = new { profileId = account.Id, pendingDeviceCount = failed.Count };
            if (failed.Count > 0) CompleteCurrent("로그인은 완료했지만 일부 장치의 인증 동기화가 보류되었습니다.", result);
            return result;
        }
        finally
        {
            _loginCancellation = null;
            if (account.IsPending && !_auth.ProfileArtifactExists(account.Id) && c.Accounts.Count > 1)
                _config.RemoveAccount(c, account.Id);
        }
    });

    private async Task<object?> ImportAsync(JsonElement p, CancellationToken token)
    {
        var path = Wire.Text(p, "path");
        if (!Path.IsPathFullyQualified(path)) throw new RpcException("invalid_path", "가져올 인증 파일의 절대 경로가 필요합니다.");
        using var lease = await ControllerMutationLock.AcquireAsync(_paths, cancellationToken: token);
        var c = _config.LoadOrCreate();
        var account = OptionalProfile(p) is { } id ? c.Accounts.FirstOrDefault(a => a.Id == id)
            ?? throw new RpcException("account_unavailable", "계정 항목을 찾지 못했습니다.") : _config.ReserveAccount(c);
        new LoginTransactionStore(_paths).ImportAuth(_auth, path, account.Id, Wire.Flag(p, "replaceExisting", !account.IsPending));
        _config.UpdateAccountEmail(c, account.Id, _auth.ReadCredentials(account.Id).Email);
        var failed = await _ssh.SyncProfileAsync(c, account.Id, token);
        var result = new { profileId = account.Id, pendingDeviceCount = failed.Count };
        if (failed.Count > 0) CompleteCurrent("인증은 등록했지만 일부 장치의 동기화가 보류되었습니다.", result);
        return result;
    }

    private async Task<AccountIdentityHashes> IdentityHashesAsync(CancellationToken token)
    {
        lock (_state) if (_busy) throw new RpcException("busy", "계정 작업을 완료한 뒤 다시 확인해 주세요.");
        using var lease = await ControllerMutationLock.AcquireAsync(_paths, cancellationToken: token);
        lock (_state) if (_busy) throw new RpcException("busy", "계정 작업을 완료한 뒤 다시 확인해 주세요.");
        var configuration = _config.LoadOrCreate();
        if (configuration.Accounts.Any(a => a.IsPending || a.NeedsLogin))
            throw new RpcException("account_unavailable", "모든 계정의 로그인을 먼저 완료해 주세요.");
        var profiles = configuration.Accounts.Select(a =>
        {
            var credentials = _auth.ReadCredentials(a.Id);
            return new AccountIdentityHash(a.Id, IdentityHash(credentials.AccountId), IdentityHash(credentials.Email.Trim().ToLowerInvariant()));
        }).ToArray();
        return new(profiles, Revision(configuration), IdentityHash(_paths.CodexHome));
    }

    private async Task<object?> MaintainAuthAsync(int? profileId, bool refresh, CancellationToken token)
    {
        using var lease = await ControllerMutationLock.AcquireAsync(_paths, cancellationToken: token);
        var c = _config.LoadOrCreate();
        var accounts = c.Accounts.Where(a => !a.IsPending && !a.NeedsLogin && (profileId is null || a.Id == profileId));
        var pending = 0;
        var deferred = 0;
        var results = new List<object>();
        foreach (var account in accounts)
        {
            if (refresh)
            {
                var maintenance = await new CodexAuthMaintenanceService(_paths, _auth, _local).RefreshAsync(account.Id, token);
                if (maintenance.DidDefer)
                {
                    deferred++;
                    results.Add(new { profileId = account.Id, state = "deferred", pendingDeviceCount = 0 });
                    continue;
                }
            }
            var failed = (await _ssh.SyncProfileAsync(c, account.Id, token)).Count;
            pending += failed;
            results.Add(new { profileId = account.Id, state = failed == 0 ? "completed" : "pending", pendingDeviceCount = failed });
        }
        var result = new { pendingDeviceCount = pending, deferredAccountCount = deferred, accounts = results };
        if (pending > 0 || deferred > 0) CompleteCurrent("일부 인증 유지 작업이 보류되었습니다. 실행 중인 Codex와 장치 상태를 확인해 주세요.", result);
        return result;
    }

    private object SaveDevice(JsonElement p)
    {
        var c = _config.LoadOrCreate();
        var draft = Wire.Item(p, "device").Deserialize<SshDeviceConfiguration>(Wire.Json)
            ?? throw new RpcException("invalid_device", "장치 설정을 입력해 주세요.");
        if (string.IsNullOrWhiteSpace(draft.Id)) draft.Id = "device-" + Guid.NewGuid().ToString("N")[..12];
        var prepared = _ssh.PrepareForSave(c, draft, Wire.Text(p, "password"), Wire.Text(p, "passphrase"), Wire.Flag(p, "clearPassword"), Wire.Flag(p, "clearPassphrase"));
        var committed = false;
        try
        {
            _config.UpsertDevice(c, prepared.Device);
            committed = true;
            foreach (var intent in prepared.SecretCleanupIntents)
            {
                if (intent.CredentialId != prepared.Device.CredentialId) _ssh.DeleteSecrets(intent.CredentialId);
                _ssh.CompleteSecretCleanup(intent.Path);
            }
            return prepared.Device;
        }
        catch
        {
            if (!committed)
                foreach (var intent in prepared.SecretCleanupIntents)
                    try
                    {
                        if (intent.CredentialId == prepared.Device.CredentialId) _ssh.DeleteSecrets(intent.CredentialId);
                        _ssh.CompleteSecretCleanup(intent.Path);
                    }
                    catch { }
            throw;
        }
    }

    private object DeleteDevice(JsonElement p)
    {
        var c = _config.LoadOrCreate();
        var device = RequiredDevice(p, c);
        var cleanup = device.CredentialId is { } id ? _ssh.BeginSecretCleanup(id) : null;
        _config.RemoveDevice(c, device.Id);
        _ssh.DeleteSecrets(device);
        if (cleanup is not null) _ssh.CompleteSecretCleanup(cleanup);
        return new { removed = true };
    }

    private async Task<object?> TrustDeviceAsync(JsonElement p, CancellationToken token)
    {
        var trust = new SshHostTrust();
        if (Wire.Item(p, "approvedKey").ValueKind == JsonValueKind.Object)
        {
            lock (_state) { ThrowIfStopping(); if (_busy) throw new RpcException("busy", "진행 중인 작업을 먼저 완료해 주세요."); }
            using var lease = await ControllerMutationLock.AcquireAsync(_paths, cancellationToken: token);
            lock (_state) { ThrowIfStopping(); if (_busy) throw new RpcException("busy", "진행 중인 작업을 먼저 완료해 주세요."); }
            var device = RequiredDevice(p);
            var approved = Wire.Item(p, "approvedKey").Deserialize<SshHostKey>(Wire.Json) ?? throw new RpcException("invalid_request", "승인한 서버 지문이 필요합니다.");
            await trust.TrustAsync(device, approved, token);
        }
        var key = await trust.InspectAsync(RequiredDevice(p), token);
        return new { trusted = key is null, hostKey = key };
    }

    private async Task<object?> BootstrapAsync(string id, CancellationToken token)
    {
        using var lease = await ControllerMutationLock.AcquireAsync(_paths, cancellationToken: token);
        var c = _config.LoadOrCreate();
        var device = c.Devices.Single(d => d.Id == id);
        var trust = await new SshHostTrust().InspectAsync(device, token);
        if (trust is not null) throw new RpcException("trust_required", "서버 지문을 먼저 확인해 주세요.", new { hostKey = trust });
        var test = await _ssh.TestConnectionAsync(device, token);
        if (!test.IsReachable) throw new RpcException("connection_failed", "SSH 연결 또는 인증에 실패했습니다.");
        var prerequisites = await _ssh.InspectPrerequisitesAsync(device, token);
        if (prerequisites.Missing.Count > 0)
            throw new RpcException("remote_prerequisites", "원격 장치에 필요한 도구가 없습니다: " + string.Join(", ", prerequisites.Missing)
                + ". 해당 장치에서 설치한 뒤 다시 시도해 주세요.");
        var bootstrap = await _ssh.BootstrapAsync(c, device, _local.GetActiveProfileId(c.Accounts), token);
        var store = new DeviceActivationTransactionStore(_paths);
        var intent = store.Save(device);
        try
        {
            _config.BeginDeviceActivation(c, device);
            var statuses = await _ssh.FetchStatusesAsync(c, _local.GetActiveProfileId(c.Accounts), token);
            var current = statuses.FirstOrDefault(d => d.Id == id);
            if (current is null || !current.IsReachable || current.ProfileId != bootstrap.ActiveProfileId
                || current.AccountId != Fingerprint(_auth.ReadCredentials(bootstrap.ActiveProfileId).AccountId))
                throw new RpcException("verification_failed", "장치 활성화 결과를 확인하지 못했습니다.");
            store.Delete(intent);
        }
        catch
        {
            _config.RollbackDeviceActivation(c, device);
            store.Delete(intent);
            throw;
        }
        await RefreshDevicesAsync(token, allowBusy: true);
        var result = new { activated = true, prerequisites.NodeAvailable, prerequisites.CodexAvailable };
        CompleteCurrent(!prerequisites.CodexAvailable ? "장치는 활성화했지만 원격 Codex CLI가 없습니다. 해당 장치에 CLI를 설치해 주세요."
            : !prerequisites.NodeAvailable ? "장치를 활성화했습니다. Node.js 없이 jq로 원격 토큰 사용량을 집계합니다."
            : "장치를 설치하고 활성화했습니다.", result);
        return result;
    }

    private ICliManagementService CliService(string deviceId)
    {
        if (deviceId is "local" or "windows") return new WindowsCliManagementService(_paths, _local);
        var id = deviceId.StartsWith("ssh:", StringComparison.Ordinal) ? deviceId[4..] : deviceId;
        var device = _config.LoadOrCreate().Devices.SingleOrDefault(d => d.Id == id && d.Enabled)
            ?? throw new RpcException("device_unavailable", "활성 SSH 장치를 선택해 주세요.");
        return new RemoteCliManagementService((update, ct) => _ssh.RunCodexHelperAsync(device, update, ct));
    }

    private async Task<object?> UpdateAllCliAsync(CancellationToken token)
    {
        using var lease = await ControllerMutationLock.AcquireAsync(_paths, cancellationToken: token);
        var targets = _config.LoadOrCreate().Devices.Where(d => d.Enabled)
            .Select(d => new CliUpdateTarget("ssh:" + d.Id, d.DisplayLabel, CliService("ssh:" + d.Id))).ToList();
        targets.Insert(0, new("local", "이 Ubuntu PC", CliService("local")));
        var raw = await new CliBatchUpdater().RunAsync(targets, new Progress<CliDeviceUpdate>(u => SetMessage(u.DeviceId + ": " + u.State)), token);
        var results = raw.Select(r => r.State == CliDeviceUpdateState.Failed
            ? r with { Message = "업데이트를 완료하지 못했습니다. 해당 장치의 설치와 연결 상태를 확인해 주세요." } : r).ToArray();
        if (results.Any(r => r.State is CliDeviceUpdateState.Failed or CliDeviceUpdateState.ReconnectPending or CliDeviceUpdateState.Skipped))
            CompleteCurrent("일부 CLI 업데이트 또는 재연결이 완료되지 않았습니다. 장치별 결과를 확인해 주세요.", results,
                results.Any(r => r.State == CliDeviceUpdateState.Failed) ? "failed" : "completed");
        return results;
    }

    private async Task<object?> TokensAsync(CancellationToken token)
    {
        var result = await new TokenUsageService(_paths).FetchAsync(_config.LoadOrCreate(), _ssh, token);
        // Collector errors may include external helper text: expose a stable message only.
        return new { devices = result.Devices.Select(d => new { id = d.Id == "windows" ? "local" : d.Id, displayName = d.Id == "windows" ? "이 Ubuntu PC" : d.DisplayName, d.IsReachable, d.Summary, d.EstimatedCostUsd, error = d.Error is null ? null : "사용량 수집을 완료하지 못했습니다." }), result.CollectedAt, result.EstimatedCostUsd };
    }

    private object Settings()
    {
        var usage = new UsageDisplayPreferencesStore(_paths).LoadUsagePreferences();
        var anchor = _controller.WeeklyAnchor.Load();
        return new { usage.FiveHour, usage.CodexWeekly, launchAtLogin = File.Exists(AutostartPath), weekly = anchor, stateDirectory = _paths.StateRoot, codexHome = _paths.CodexHome };
    }

    private string AutostartPath => Path.Combine((!_paths.IsIsolated ? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") : null)
        ?? Path.Combine(_paths.Home, ".config"), "autostart", "codex-syncbar.desktop");
    private object SaveSettings(JsonElement p)
    {
        var store = new UsageDisplayPreferencesStore(_paths);
        var usage = store.LoadUsagePreferences();
        usage.FiveHour = Wire.Flag(p, "fiveHour", usage.FiveHour);
        usage.CodexWeekly = Wire.Flag(p, "codexWeekly", usage.CodexWeekly);
        store.SaveUsagePreferences(usage);
        if (Wire.Item(p, "launchAtLogin").ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            WindowsPathSafety.EnsureFile(AutostartPath, "자동 시작 항목");
            if (Wire.Flag(p, "launchAtLogin"))
            {
                WindowsPathSafety.EnsureDirectory(Path.GetDirectoryName(AutostartPath)!, "자동 시작 디렉터리");
                File.WriteAllText(AutostartPath, "[Desktop Entry]\nType=Application\nName=Codex SyncBar\nExec=codex-syncbar --background\nX-GNOME-Autostart-enabled=true\n");
            }
            else if (File.Exists(AutostartPath)) File.Delete(AutostartPath);
        }
        return Settings();
    }

    private async Task<T> MutateAsync<T>(Func<T> action, CancellationToken token)
    {
        lock (_state)
        {
            ThrowIfStopping();
            if (_busy) throw new RpcException("busy", "진행 중인 작업을 먼저 완료해 주세요.");
            if (_operation?.State == "recoveryRequired" || _error is not null) throw new RpcException("recovery_required", "이전 작업의 복구를 먼저 완료해 주세요.");
        }
        using var lease = await ControllerMutationLock.AcquireAsync(_paths, cancellationToken: token);
        lock (_state)
        {
            ThrowIfStopping();
            if (_busy) throw new RpcException("busy", "진행 중인 작업을 먼저 완료해 주세요.");
            if (_operation?.State == "recoveryRequired" || _error is not null) throw new RpcException("recovery_required", "이전 작업의 복구를 먼저 완료해 주세요.");
            T value;
            try { value = action(); }
            catch (CodexSyncBarException error) { throw new RpcException("invalid_setting", error.Message); }
            _deviceGeneration++;
            _previews.Clear();
            return value;
        }
    }

    private OperationView StartJob(string kind, int? profileId, Func<CancellationToken, Task<object?>> action, bool allowRecovery = false)
    {
        lock (_state)
        {
            ThrowIfStopping();
            if (_busy) throw new RpcException("busy", "진행 중인 작업을 먼저 완료해 주세요.");
            if (!allowRecovery && (_operation?.State == "recoveryRequired" || _error is not null)) throw new RpcException("recovery_required", "이전 작업의 복구를 먼저 완료해 주세요.");
            _busy = true;
            _deviceGeneration++;
            _operation = new(Guid.NewGuid().ToString("N"), kind, profileId, "queued", "작업을 준비하고 있습니다.", [], DateTimeOffset.UtcNow);
            var accepted = _operation;
            _history[accepted.Id] = accepted;
            _job = Task.Run(async () =>
            {
                SetOperation(accepted with { State = "running", Message = "작업을 진행하고 있습니다." });
                try
                {
                    var result = await action(_lifetime.Token);
                    lock (_state)
                        if (_operation is { IsComplete: false } current) SetOperation(current with { State = "completed", Message = "작업을 완료했습니다.", Result = result, FinishedAt = DateTimeOffset.UtcNow });
                }
                catch (OperationCanceledException)
                {
                    lock (_state) if (_operation is { IsComplete: false } current) SetOperation(current with { State = "cancelled", Message = "작업을 취소했습니다.", FinishedAt = DateTimeOffset.UtcNow });
                }
                catch (Exception e)
                {
                    lock (_state)
                        if (_operation is { State: not "recoveryRequired" } current)
                            SetOperation(current with { State = "failed", Message = e is RpcException rpc ? rpc.Message : "작업을 완료하지 못했습니다. 계정과 장치 상태를 확인해 주세요.", ErrorCode = e is RpcException r ? r.Code : "operation_failed", FinishedAt = DateTimeOffset.UtcNow });
                }
                finally
                {
                    lock (_state) { _busy = false; _deviceGeneration++; _previews.Clear(); }
                }
            });
            return accepted;
        }
    }

    private void SetOperation(OperationView value)
    {
        lock (_state)
        {
            if (_busy && _operation is { } accepted && accepted.Kind == value.Kind && accepted.Id != value.Id)
                value = value with { Id = accepted.Id, StartedAt = accepted.StartedAt };
            _operation = value;
            _history[value.Id] = value;
            foreach (var requestId in _appliedRequests.Where(p => p.Value.Operation.Id == value.Id).Select(p => p.Key).ToArray())
                _appliedRequests[requestId] = (_appliedRequests[requestId].PreviewId, value);
            if (_history.Count > 100) _history.Remove(_history.Keys.First());
        }
    }
    private void SetMessage(string message) { lock (_state) if (_operation is { IsComplete: false } o) SetOperation(o with { Message = message }); }
    private void CompleteCurrent(string message, object? result, string state = "completed")
    {
        lock (_state) if (_operation is { } o) SetOperation(o with { State = state, Message = message, Result = result, FinishedAt = DateTimeOffset.UtcNow });
    }
    private static OperationView FromSwitch(SwitchOperation o, string kind) => new(o.Id, kind, o.ProfileId, o.State, o.Message.Replace("Windows", "Ubuntu"),
        o.Targets.Select(t => t with { Id = t.Id == "windows" ? "local" : t.Id, DisplayName = t.Id == "windows" ? "이 Ubuntu PC" : t.DisplayName }).ToArray(), o.StartedAt, o.FinishedAt);
    private static string Revision(AppConfiguration c) => SyncBarController.ComputeRevision(c, []);
    private static int? OptionalProfile(JsonElement p) => Wire.Number(p, "profileId") is > 0 and var id ? id : null;
    private static AccountProfile RequiredAccount(AppConfiguration c, JsonElement p) => c.Accounts.FirstOrDefault(a => a.Id == Wire.Number(p, "profileId")) ?? throw new RpcException("account_unavailable", "계정 항목을 찾지 못했습니다.");
    private SshDeviceConfiguration RequiredDevice(JsonElement p, AppConfiguration? c = null)
    {
        var id = Wire.Text(p, "deviceId");
        if (id.StartsWith("ssh:", StringComparison.Ordinal)) id = id[4..];
        return (c ?? _config.LoadOrCreate()).Devices.FirstOrDefault(d => d.Id == id) ?? throw new RpcException("device_unavailable", "SSH 장치를 찾지 못했습니다.");
    }
    private static string NormalizeDeviceId(string id) => id == "local" || id.StartsWith("ssh:", StringComparison.Ordinal) ? id : "ssh:" + id;
    private static string AccountLabel(AppConfiguration c, int? id) => c.Accounts.FirstOrDefault(a => a.Id == id) is { } a ? a.CustomAlias ?? a.Email : "알 수 없음";
    private static string Fingerprint(string accountId) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(accountId)))[..12];
    private static string IdentityHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string ObservationKey(IEnumerable<DeviceView> devices) => JsonSerializer.Serialize(devices.OrderBy(d => d.Id).Select(d => new { d.Id, d.Enabled, d.IsReachable, d.CurrentProfileId, d.Status }), Wire.Json);
    private void ThrowIfStopping()
    {
        if (_shutdownRequested) throw new RpcException("shutting_down", "진행 중인 작업을 정리하고 종료하고 있습니다.");
    }
    public async Task StopAsync()
    {
        lock (_state) _shutdownRequested = true;
        _lifetime.Cancel();
        _controller.Dispose();
        try { await _initialization; } catch (OperationCanceledException) { }
        Task? job;
        lock (_state) job = _job;
        if (job is not null) await job;
        await _controller.StopAsync();
    }
    public void Dispose() { _lifetime.Cancel(); _controller.Dispose(); }
}
