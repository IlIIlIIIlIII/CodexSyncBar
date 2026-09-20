namespace CodexSyncBar.Windows.Core;

// Shared by foreground, tray and widget-triggered refreshes.
public sealed class WeeklyAnchorCoordinator
{
    private readonly WindowsPaths _paths;
    private readonly WeeklyAnchorStore _store;
    private readonly Func<int, CancellationToken, Task<string>> _send;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<bool> _canSend;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _running = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _evaluating = new();
    public event EventHandler? Changed;

    public WeeklyAnchorCoordinator(WindowsPaths paths, Func<int, CancellationToken, Task<string>> send,
        Func<DateTimeOffset>? now = null, Func<bool>? canSend = null)
    {
        _paths = paths; _store = new(paths); _send = send; _now = now ?? (() => DateTimeOffset.UtcNow);
        _canSend = canSend ?? (() => true);
    }

    public bool IsRunning(int id) => _running.ContainsKey(id);
    public WeeklyAnchorState Load() => _store.Load();

    public async Task SetEnabledAsync(int id, bool enabled, CancellationToken token = default)
    {
        using (await ControllerMutationLock.AcquireAsync(_paths, cancellationToken: token))
        {
            var configuration = new ConfigurationStore(_paths).LoadOrCreate();
            if (!configuration.Accounts.Any(a => a.Id == id && !a.IsPending && !a.NeedsLogin))
                throw new CodexSyncBarException("주간 메시지를 설정할 계정이 없거나 재로그인이 필요합니다.");
            var state = _store.Load();
            state.Preferences.SetEnabled(id, enabled);
            _store.Save(state);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> EvaluateAsync(UsageSnapshot snapshot, bool manual = false, CancellationToken token = default)
    {
        var id = snapshot.ProfileId;
        var now = _now();
        if (!manual && !_store.Load().Preferences.IsEnabled(id)) return false;
        if (snapshot.Weekly is null || snapshot.UpdatedAt > now.AddMinutes(1)
            || now - snapshot.UpdatedAt > TimeSpan.FromMinutes(5) || !_evaluating.TryAdd(id, 0)) return false;
        try
        {
            using var mutation = await ControllerMutationLock.AcquireAsync(_paths, cancellationToken: token);
            if (!_canSend()) return false;
            // Recheck after waiting: queued work must respect a disabled/deleted account.
            var configuration = new ConfigurationStore(_paths).LoadOrCreate();
            if (!configuration.Accounts.Any(a => a.Id == id && !a.IsPending && !a.NeedsLogin)) return false;
            if (new AccountSwitchCoordinator(_paths).ReadOperation()?.State == "recoveryRequired") return false;
            var state = _store.Load();
            var record = state.Records.GetValueOrDefault(id) ?? new WeeklyAnchorRecord();
            now = _now();
            if (now - snapshot.UpdatedAt > TimeSpan.FromMinutes(5)) return false;
            var decision = manual ? WeeklyAnchorDecision.Trigger
                : WeeklyAnchorDecisionEngine.Decide(state.Preferences.IsEnabled(id), snapshot.Weekly, record, now);
            if (decision == WeeklyAnchorDecision.None) return false;
            var next = snapshot.Weekly.ResetsAt is { } future && future > now ? future : (DateTimeOffset?)null;
            if (decision == WeeklyAnchorDecision.ConfirmResetDrift)
            {
                record.ResetDriftCandidateAt = snapshot.Weekly.ResetsAt;
                record.ResetDriftObservationCount++;
            }
            else if (decision is WeeklyAnchorDecision.Observe or WeeklyAnchorDecision.AlreadyActive)
            {
                if (decision == WeeklyAnchorDecision.AlreadyActive) record.LastHandledResetAt = record.NextResetAt;
                record.NextResetAt = next;
                record.ResetDriftCandidateAt = null;
                record.ResetDriftObservationCount = 0;
                record.LastError = null;
            }
            else
            {
                var expectedReset = record.NextResetAt;
                record.LastAttemptAt = now;
                record.LastAttemptConfigurationId = WeeklyAnchorConfiguration.Id;
                record.LastError = null;
                state.Records[id] = record;
                _store.Save(state); // Durable before dispatch: restart observes retry cooldown.
                _running.TryAdd(id, 0);
                Changed?.Invoke(this, EventArgs.Empty);
                try
                {
                    await _send(id, token);
                    record.LastSuccessAt = _now();
                    record.LastHandledResetAt = expectedReset;
                    record.NextResetAt = next > _now() ? next : null;
                    record.ResetDriftCandidateAt = null;
                    record.ResetDriftObservationCount = 0;
                }
                catch (Exception error)
                {
                    record.LastError = error is OperationCanceledException
                        ? "전송이 중단되었습니다. 다음 확인 때 재시도합니다." : error.Message;
                    _store.Save(state);
                    throw;
                }
            }
            state.Records[id] = record;
            _store.Save(state);
            return decision == WeeklyAnchorDecision.Trigger;
        }
        finally
        {
            _running.TryRemove(id, out _);
            _evaluating.TryRemove(id, out _);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
