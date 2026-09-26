using System.Text.Json;
using System.Collections.Concurrent;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class AccountSwitchTests
{
    [Fact]
    public async Task PreflightsEveryTargetThenAppliesAndVerifiesAllInParallel()
    {
        using var fixture = new Fixture();
        var windows = fixture.Target("windows", local: true);
        var ssh = fixture.Target("ssh");
        var wsl = fixture.Target("wsl");
        var prepare = new PhaseBarrier(3);
        var apply = new PhaseBarrier(3);
        var verify = new PhaseBarrier(3);
        foreach (var target in new[] { windows, ssh, wsl })
        {
            target.PrepareWait = prepare.ArriveAsync;
            target.ApplyWait = apply.ArriveAsync;
            target.VerifyWait = verify.ArriveAsync;
        }
        var result = await fixture.Coordinator.SwitchAsync(2, [windows, ssh, wsl]);
        Assert.Equal("completed", result.State);
        var events = fixture.Events.Where(value => !value.EndsWith(":release")).ToArray();
        Assert.Equal(9, events.Length);
        Assert.All(events.Take(3), value => Assert.EndsWith(":prepare", value));
        Assert.All(events.Skip(3).Take(3), value => Assert.EndsWith(":apply", value));
        Assert.All(events.Skip(6), value => Assert.EndsWith(":verify", value));
        Assert.All(result.Targets, target => Assert.Equal("verified", target.State));
        var journal = JsonSerializer.Deserialize<SwitchJournal>(File.ReadAllText(fixture.Coordinator.JournalPath))!;
        Assert.Equal(3, journal.Checkpoints.Count);
        Assert.All(journal.Checkpoints, checkpoint => Assert.True(checkpoint.Attempted));
    }

    [Fact]
    public async Task PreflightFailureNeverMutatesAnyTarget()
    {
        using var fixture = new Fixture();
        var ssh = fixture.Target("ssh");
        var wsl = fixture.Target("wsl");
        wsl.FailAt = "prepare";
        var result = await fixture.Coordinator.SwitchAsync(2, [ssh, wsl, fixture.Target("windows", local: true)]);
        Assert.Equal("failed", result.State);
        Assert.DoesNotContain(fixture.Events, value => value.EndsWith(":apply") || value.EndsWith(":restore"));
    }

    [Fact]
    public async Task DisconnectAfterRemoteWriteRestoresEveryAttemptedTarget()
    {
        using var fixture = new Fixture();
        var ssh = fixture.Target("ssh");
        var wsl = fixture.Target("wsl");
        wsl.FailAt = "apply";
        var result = await fixture.Coordinator.SwitchAsync(2, [ssh, wsl, fixture.Target("windows", local: true)]);
        Assert.Equal("failed", result.State);
        Assert.Equal(new[] { "windows:restore", "wsl:restore", "ssh:restore" }, fixture.Events.Where(value => value.EndsWith(":restore")));
        Assert.Equal(1, ssh.Active);
        Assert.Equal(1, wsl.Active);
        Assert.Contains("windows:apply", fixture.Events);
    }

    [Fact]
    public async Task RollbackWaitsForLateWriterAfterAnotherDeviceFails()
    {
        using var fixture = new Fixture();
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = fixture.Target("slow");
        slow.ApplyWait = () => releaseWriter.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var failing = fixture.Target("failing");
        failing.FailAt = "apply";
        failing.AfterApply = () => failureReached.TrySetResult();
        var operation = fixture.Coordinator.SwitchAsync(2, [slow, failing]);
        try
        {
            await failureReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(operation.IsCompleted);
            Assert.DoesNotContain(fixture.Events, value => value.EndsWith(":restore"));
        }
        finally { releaseWriter.TrySetResult(); }
        var result = await operation;
        Assert.Equal("failed", result.State);
        Assert.Equal(1, slow.Active);
        Assert.Equal(1, failing.Active);
        Assert.All(result.Targets, target => Assert.Equal("restored", target.State));
    }

    [Fact]
    public async Task FailedPreflightWaitsForAndReleasesLateCheckpointWithoutApplying()
    {
        using var fixture = new Fixture();
        var releasePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = fixture.Target("slow");
        slow.PrepareWait = () => releasePreparation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var failing = fixture.Target("failing");
        failing.FailAt = "prepare";
        var operation = fixture.Coordinator.SwitchAsync(2, [slow, failing]);
        try
        {
            Assert.False(operation.IsCompleted);
            Assert.DoesNotContain(fixture.Events, value => value.EndsWith(":apply"));
        }
        finally { releasePreparation.TrySetResult(); }
        Assert.Equal("failed", (await operation).State);
        Assert.Contains("slow:release", fixture.Events);
        Assert.DoesNotContain(fixture.Events, value => value.EndsWith(":apply") || value.EndsWith(":restore"));
    }

    [Fact]
    public async Task VerificationFailureRestoresWindowsThenRemoteTargets()
    {
        using var fixture = new Fixture();
        var ssh = fixture.Target("ssh");
        ssh.FailAt = "verify";
        var windows = fixture.Target("windows", local: true);
        var result = await fixture.Coordinator.SwitchAsync(2, [ssh, windows]);
        Assert.Equal("failed", result.State);
        Assert.Equal(new[] { "windows:restore", "ssh:restore" }, fixture.Events.Where(value => value.EndsWith(":restore")));
    }

    [Fact]
    public async Task FailedRollbackPersistsForNextProcessAndBlocksNewSwitch()
    {
        using var fixture = new Fixture();
        var ssh = fixture.Target("ssh");
        var windows = fixture.Target("windows", local: true);
        ssh.FailRestore = true;
        windows.FailAt = "apply";
        var result = await fixture.Coordinator.SwitchAsync(2, [ssh, windows]);
        Assert.Equal("recoveryRequired", result.State);
        await Assert.ThrowsAsync<CodexSyncBarException>(() => fixture.Coordinator.SwitchAsync(3, [ssh, windows]));
        ssh.FailRestore = false;
        var restarted = new AccountSwitchCoordinator(fixture.Paths);
        var recovered = await restarted.RecoverAsync([ssh, windows]);
        Assert.Equal("failed", recovered!.State);
        Assert.Equal(1, ssh.Active);
        Assert.All(recovered.Targets, target => Assert.Equal("restored", target.State));
    }

    [Fact]
    public async Task CrashJournalRecoversAttemptedWriteAndRejectsChangedEndpoint()
    {
        using var fixture = new Fixture();
        var ssh = fixture.Target("ssh");
        ssh.Active = 2;
        fixture.Paths.EnsureDirectories();
        File.WriteAllText(fixture.Coordinator.JournalPath, JsonSerializer.Serialize(new SwitchJournal
        {
            Operation = new SwitchOperation { Id = "operation", ProfileId = 2, State = "applying", Targets = [new("ssh", "ssh", "applying")] },
            Checkpoints = [new() { Id = "ssh", Fingerprint = "old-endpoint", Value = "1", Attempted = true }],
        }));
        var result = await fixture.Coordinator.RecoverAsync([ssh]);
        Assert.Equal("recoveryRequired", result!.State);
        Assert.Equal(2, ssh.Active);
        Assert.DoesNotContain("ssh:restore", fixture.Events);
        ssh.ConfigurationFingerprint = "old-endpoint";
        result = await fixture.Coordinator.RecoverAsync([ssh]);
        Assert.Equal("failed", result!.State);
        Assert.Equal(1, ssh.Active);
    }

    [Fact]
    public async Task CancellationAfterApplyRollsBackWithIndependentCancellation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var ssh = fixture.Target("ssh");
        ssh.AfterApply = cancellation.Cancel;
        var result = await fixture.Coordinator.SwitchAsync(2, [ssh, fixture.Target("windows", local: true)], cancellationToken: cancellation.Token);
        Assert.Equal("failed", result.State);
        Assert.Equal(1, ssh.Active);
    }

    [Fact]
    public async Task CommittedJournalNeverRollsBackOnRestart()
    {
        using var fixture = new Fixture();
        var ssh = fixture.Target("ssh");
        await fixture.Coordinator.SwitchAsync(2, [ssh]);
        fixture.Events.Clear();
        await new AccountSwitchCoordinator(fixture.Paths).RecoverAsync([ssh]);
        Assert.Equal(2, ssh.Active);
        Assert.DoesNotContain("ssh:restore", fixture.Events);
    }

    [Fact]
    public async Task FailuresDoNotLeakHelperOutputIntoWidgetOrJournal()
    {
        using var fixture = new Fixture();
        var target = fixture.Target("ssh");
        target.FailAt = "apply";
        target.FailRestore = true;
        var result = await fixture.Coordinator.SwitchAsync(2, [target]);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("test-access-secret", serialized);
        Assert.DoesNotContain("test-access-secret", File.ReadAllText(fixture.Coordinator.JournalPath));
    }

    [Fact]
    public void RevisionChangesWithTargetConfigurationAndMasksWidgetEmails()
    {
        var configuration = new AppConfiguration { Accounts = [new() { Id = 1, Email = "someone@example.com" }] };
        var wsl = new WslDeviceConfiguration { Distribution = "Ubuntu", Username = "user", HomeDirectory = "/home/user", IsInstalled = true };
        var before = SyncBarController.ComputeRevision(configuration, [wsl]);
        wsl.Enabled = true;
        Assert.NotEqual(before, SyncBarController.ComputeRevision(configuration, [wsl]));
        Assert.Equal("s***@example.com", SyncBarController.MaskEmail(configuration.Accounts[0].Email));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-switch-tests-" + Guid.NewGuid().ToString("N"));
        public WindowsPaths Paths { get; }
        public AccountSwitchCoordinator Coordinator { get; }
        public ConcurrentQueue<string> Events { get; } = new();
        public Fixture()
        {
            Paths = new WindowsPaths(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
            Coordinator = new(Paths);
        }
        public FakeTarget Target(string id, bool local = false) => new(id, local, Events, Coordinator.JournalPath);
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }

    private sealed class PhaseBarrier(int participants)
    {
        private readonly TaskCompletionSource _allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = participants;
        public Task ArriveAsync()
        {
            if (Interlocked.Decrement(ref _remaining) == 0) _allStarted.TrySetResult();
            return _allStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class FakeTarget(string id, bool isLocal, ConcurrentQueue<string> events, string journalPath) : IAccountTarget
    {
        public string Id => id;
        public string DisplayName => id;
        public string ConfigurationFingerprint { get; set; } = "endpoint";
        public bool IsLocal => isLocal;
        public int Active { get; set; } = 1;
        public string? FailAt { get; set; }
        public bool FailRestore { get; set; }
        public Action? AfterApply { get; set; }
        public Func<Task>? PrepareWait { get; set; }
        public Func<Task>? ApplyWait { get; set; }
        public Func<Task>? VerifyWait { get; set; }
        public async Task<string> PrepareAsync(int profileId, CancellationToken cancellationToken)
        {
            events.Enqueue(id + ":prepare");
            if (PrepareWait is not null) await PrepareWait();
            if (FailAt == "prepare") throw new IOException("preflight failure");
            return Active.ToString();
        }
        public async Task ApplyAsync(int profileId, CancellationToken cancellationToken)
        {
            using (var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var journal = JsonSerializer.Deserialize<SwitchJournal>(stream);
                Assert.True(journal!.Checkpoints.Single(checkpoint => checkpoint.Id == id).Attempted);
            }
            events.Enqueue(id + ":apply");
            if (ApplyWait is not null) await ApplyWait();
            Active = profileId;
            AfterApply?.Invoke();
            if (FailAt == "apply") throw new IOException("connection lost after write: test-access-secret");
        }
        public async Task VerifyAsync(int profileId, CancellationToken cancellationToken)
        {
            events.Enqueue(id + ":verify");
            if (VerifyWait is not null) await VerifyWait();
            if (FailAt == "verify") throw new IOException("verification failure");
            Assert.Equal(profileId, Active);
        }
        public Task RestoreAsync(string checkpoint, CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.IsCancellationRequested);
            events.Enqueue(id + ":restore");
            if (FailRestore) throw new IOException("device offline: test-access-secret");
            Active = int.Parse(checkpoint);
            return Task.CompletedTask;
        }
        public Task ReleaseAsync(string checkpoint, CancellationToken cancellationToken)
        {
            events.Enqueue(id + ":release");
            return Task.CompletedTask;
        }
    }
}
