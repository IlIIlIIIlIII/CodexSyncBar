using System.Text.Json;

namespace CodexSyncBar.Windows.Core;

/// <summary>Checkpoints contain only opaque local secret references or profile IDs, never credentials.</summary>
public interface IAccountTarget
{
    string Id { get; }
    string DisplayName { get; }
    string ConfigurationFingerprint { get; }
    bool IsLocal { get; }
    Task<string> PrepareAsync(int profileId, CancellationToken cancellationToken);
    Task ApplyAsync(int profileId, CancellationToken cancellationToken);
    Task VerifyAsync(int profileId, CancellationToken cancellationToken);
    Task RestoreAsync(string checkpoint, CancellationToken cancellationToken);
    Task ReleaseAsync(string checkpoint, CancellationToken cancellationToken);
}

public sealed class SwitchJournal
{
    public int SchemaVersion { get; set; } = 1;
    public int? RemovedProfileId { get; set; }
    public bool LogoutConfigurationCommitted { get; set; }
    public SwitchOperation Operation { get; set; } = new();
    public List<SwitchCheckpoint> Checkpoints { get; set; } = [];
}

public sealed class SwitchCheckpoint
{
    public string Id { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Attempted { get; set; }
    public bool Restored { get; set; }
}

/// <summary>Caller holds ControllerMutationLock for the entire operation, including recovery.</summary>
public sealed class AccountSwitchCoordinator(WindowsPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _journalGate = new();
    public string JournalPath => Path.Combine(paths.StateRoot, "switch-operation.json");
    public event Action<SwitchOperation>? Progress;

    public SwitchOperation? ReadOperation() => ReadJournal()?.Operation;
    public int? ReadLogoutProfileId() => ReadJournal()?.RemovedProfileId;

    internal void ReconcileCompletedLogout(ConfigurationStore store, AppConfiguration configuration, AuthStore auth)
    {
        var journal = ReadJournal();
        if (journal?.Operation.State != "completed" || journal.RemovedProfileId is not { } profileId
            || journal.LogoutConfigurationCommitted) return;
        // A successful re-login can outlive an older journal. Never replay that logout over new credentials.
        if (configuration.Accounts.Any(account => account.Id == profileId) && !auth.ProfileArtifactExists(profileId))
            store.MarkAccountLoggedOut(configuration, profileId);
        journal.LogoutConfigurationCommitted = true;
        Save(journal);
    }

    public async Task<SwitchOperation> SwitchAsync(int profileId, IReadOnlyList<IAccountTarget> targets,
        string? operationId = null, CancellationToken cancellationToken = default, int? removedProfileId = null)
    {
        var previous = ReadJournal();
        if (previous is not null && previous.Operation.State != "completed"
            && previous.Checkpoints.Any(checkpoint => checkpoint.Attempted && !checkpoint.Restored))
            throw new CodexSyncBarException("이전 계정 전환의 복구를 먼저 완료해 주세요.");
        if (targets.Select(target => target.Id).Distinct(StringComparer.Ordinal).Count() != targets.Count)
            throw new CodexSyncBarException("중복된 계정 전환 대상입니다.");
        var ordered = targets.OrderBy(target => target.IsLocal).ToArray();
        var journal = new SwitchJournal
        {
            RemovedProfileId = removedProfileId,
            Operation = new SwitchOperation
            {
                Id = operationId ?? Guid.NewGuid().ToString("N"), ProfileId = profileId,
                State = "preflight", Message = "적용 대상 장치를 확인하고 있습니다.", StartedAt = DateTimeOffset.UtcNow,
                Targets = ordered.Select(target => new SwitchTargetResult(target.Id, target.DisplayName, "pending")).ToArray(),
            },
        };
        Save(journal);
        try
        {
            await Task.WhenAll(ordered.Select(async target =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string checkpoint;
                try { checkpoint = await target.PrepareAsync(profileId, cancellationToken); }
                catch
                {
                    SetTarget(journal, target.Id, "failed", "장치 사전 확인에 실패했습니다. 연결과 계정 설정을 확인해 주세요.");
                    throw;
                }
                lock (_journalGate)
                {
                    journal.Checkpoints.Add(new SwitchCheckpoint { Id = target.Id, Fingerprint = target.ConfigurationFingerprint, Value = checkpoint });
                    SetTarget(journal, target.Id, "ready");
                }
            }));
            SetState(journal, "applying", "선택한 계정을 적용하고 있습니다.");
            // WhenAll settles every in-flight write before the catch block can roll back.
            await Task.WhenAll(ordered.Select(async target =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_journalGate)
                {
                    journal.Checkpoints.Single(checkpoint => checkpoint.Id == target.Id).Attempted = true;
                    SetTarget(journal, target.Id, "applying"); // durable intent precedes the first write
                }
                try { await target.ApplyAsync(profileId, cancellationToken); }
                catch
                {
                    SetTarget(journal, target.Id, "failed", "계정 적용을 완료하지 못했습니다.");
                    throw;
                }
                SetTarget(journal, target.Id, "applied");
            }));
            SetState(journal, "verifying", "적용 대상 장치의 결과를 확인하고 있습니다.");
            await Task.WhenAll(ordered.Select(async target =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { await target.VerifyAsync(profileId, cancellationToken); }
                catch
                {
                    SetTarget(journal, target.Id, "failed", "적용된 계정을 확인하지 못했습니다.");
                    throw;
                }
                SetTarget(journal, target.Id, "verified");
            }));
            SetState(journal, "completed", ordered.Length == 1
                ? $"{ordered[0].DisplayName}에 계정을 적용했습니다." : "모든 대상 장치에 적용했습니다.", finished: true);
            await ReleaseAsync(journal, targets);
            return journal.Operation;
        }
        catch (Exception error)
        {
            var changed = journal.Checkpoints.Any(checkpoint => checkpoint.Attempted);
            return await RestoreAsync(journal, targets, error is OperationCanceledException ? "계정 전환을 취소했습니다." : changed
                ? "계정 전환을 완료하지 못해 이전 상태로 복구했습니다." : "장치 사전 확인에 실패하여 계정은 변경하지 않았습니다.");
        }
    }

    public async Task<SwitchOperation?> RecoverAsync(IReadOnlyList<IAccountTarget> targets)
    {
        var journal = ReadJournal();
        if (journal is null) return null;
        if (journal.Operation.State is "completed" or "failed")
        {
            await ReleaseAsync(journal, targets);
            return journal.Operation;
        }
        return await RestoreAsync(journal, targets, "중단된 계정 전환을 이전 상태로 복구했습니다.");
    }

    private async Task<SwitchOperation> RestoreAsync(SwitchJournal journal, IReadOnlyList<IAccountTarget> targets, string reason)
    {
        SetState(journal, "rollingBack", "이전 계정으로 복구하고 있습니다.");
        var errors = new List<string>();
        foreach (var checkpoint in journal.Checkpoints.AsEnumerable().Reverse().Where(value => value.Attempted && !value.Restored))
        {
            var target = targets.FirstOrDefault(value => value.Id == checkpoint.Id && value.ConfigurationFingerprint == checkpoint.Fingerprint);
            try
            {
                if (target is null) throw new CodexSyncBarException("장치 설정이 변경되었거나 삭제되어 자동 복구할 수 없습니다.");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                await target.RestoreAsync(checkpoint.Value, deadline.Token);
                checkpoint.Restored = true;
                SetTarget(journal, checkpoint.Id, "restored");
            }
            catch (Exception)
            {
                errors.Add(checkpoint.Id);
                SetTarget(journal, checkpoint.Id, "recoveryRequired", "장치 연결 또는 설정을 확인한 후 복구를 다시 시도해 주세요.");
            }
        }
        SetState(journal, errors.Count == 0 ? "failed" : "recoveryRequired",
            errors.Count == 0 ? reason : $"{reason} 복구 확인 필요: {string.Join(", ", errors)}", finished: true);
        if (errors.Count == 0) await ReleaseAsync(journal, targets);
        return journal.Operation;
    }

    private static async Task ReleaseAsync(SwitchJournal journal, IReadOnlyList<IAccountTarget> targets)
    {
        foreach (var checkpoint in journal.Checkpoints)
        {
            var target = targets.FirstOrDefault(value => value.Id == checkpoint.Id && value.ConfigurationFingerprint == checkpoint.Fingerprint);
            if (target is null) continue;
            try { await target.ReleaseAsync(checkpoint.Value, CancellationToken.None); }
            catch { /* Keep durable operation result; encrypted cleanup is retried on startup. */ }
        }
    }

    private void SetTarget(SwitchJournal journal, string id, string state, string? detail = null)
    {
        lock (_journalGate)
        {
            journal.Operation = journal.Operation with
            {
                Targets = journal.Operation.Targets.Select(target => target.Id == id ? target with { State = state, Detail = detail } : target).ToArray(),
            };
            Save(journal);
        }
    }

    private void SetState(SwitchJournal journal, string state, string message, bool finished = false)
    {
        journal.Operation = journal.Operation with { State = state, Message = message, FinishedAt = finished ? DateTimeOffset.UtcNow : null };
        Save(journal);
    }

    private void Save(SwitchJournal journal)
    {
        lock (_journalGate) SaveCore(journal);
    }

    private void SaveCore(SwitchJournal journal)
    {
        paths.EnsureDirectories();
        WindowsPathSafety.EnsureFile(JournalPath, "계정 전환 복구 기록");
        var temporary = JournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, journal, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, JournalPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Progress?.Invoke(journal.Operation);
    }

    private SwitchJournal? ReadJournal()
    {
        lock (_journalGate) return ReadJournalCore();
    }

    private SwitchJournal? ReadJournalCore()
    {
        WindowsPathSafety.EnsureFile(JournalPath, "계정 전환 복구 기록");
        if (!File.Exists(JournalPath)) return null;
        var journal = JsonSerializer.Deserialize<SwitchJournal>(File.ReadAllText(JournalPath));
        if (journal is null || journal.SchemaVersion != 1 || string.IsNullOrWhiteSpace(journal.Operation.Id))
            throw new CodexSyncBarException("계정 전환 복구 기록을 확인하지 못했습니다.");
        return journal;
    }
}

public sealed class WindowsAccountTarget(AuthStore authStore, LocalSwitchService localSwitch, WindowsPaths paths) : IAccountTarget
{
    private readonly WindowsSecretStore _secrets = new(paths);
    public string Id => "windows";
    public string DisplayName => "이 Windows PC";
    public string ConfigurationFingerprint => paths.ActiveAuthFile;
    public bool IsLocal => true;

    public Task<string> PrepareAsync(int profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = authStore.ReadCredentials(profileId);
        var activeAccountId = authStore.ReadActiveAccountId();
        foreach (var existing in authStore.ExistingProfileIds())
        {
            try
            {
                if (authStore.ReadCredentials(existing).AccountId == activeAccountId)
                {
                    authStore.ReconcileActiveCredentials(existing);
                    break;
                }
            }
            catch (AuthenticationRequiredException) { }
        }
        var key = "switch-backup-" + Guid.NewGuid().ToString("N");
        _secrets.Save(JsonSerializer.Serialize(authStore.ReadActiveAuth()), key);
        return Task.FromResult(key);
    }

    public Task ApplyAsync(int profileId, CancellationToken cancellationToken) => localSwitch.SwitchAsync(profileId, cancellationToken);

    public Task VerifyAsync(int profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (authStore.ReadActiveAccountId() != authStore.ReadCredentials(profileId).AccountId)
            throw new CodexSyncBarException("Windows 계정 적용 결과를 확인하지 못했습니다.");
        return Task.CompletedTask;
    }

    public async Task RestoreAsync(string checkpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = _secrets.Read(checkpoint) ?? throw new CodexSyncBarException("Windows 계정 복구 인증을 찾지 못했습니다.");
        var previous = JsonSerializer.Deserialize<CodexAuthFile>(json);
        await localSwitch.RestoreAsync(previous, cancellationToken);
    }

    public Task ReleaseAsync(string checkpoint, CancellationToken cancellationToken)
    {
        _secrets.Delete(checkpoint);
        return Task.CompletedTask;
    }
}
