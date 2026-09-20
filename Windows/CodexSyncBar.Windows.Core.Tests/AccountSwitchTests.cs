using System.Text.Json;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class AccountSwitchTests
{
    [Fact]
    public async Task PreflightsEveryTargetThenAppliesRemoteBeforeWindowsAndVerifiesAll()
    {
        using var fixture = new Fixture();
        var windows = fixture.Target("windows", local: true);
        var ssh = fixture.Target("ssh");
        var wsl = fixture.Target("wsl");
        var result = await fixture.Coordinator.SwitchAsync(2, [windows, ssh, wsl]);
        Assert.Equal("completed", result.State);
        Assert.Equal(new[] { "ssh:prepare", "wsl:prepare", "windows:prepare", "ssh:apply", "wsl:apply", "windows:apply", "ssh:verify", "wsl:verify", "windows:verify" },
            fixture.Events.Where(value => !value.EndsWith(":release")));
        Assert.All(result.Targets, target => Assert.Equal("verified", target.State));
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
    public async Task DisconnectAfterRemoteWriteRestoresAttemptedTargetsInReverseOrder()
    {
        using var fixture = new Fixture();
        var ssh = fixture.Target("ssh");
        var wsl = fixture.Target("wsl");
        wsl.FailAt = "apply";
        var result = await fixture.Coordinator.SwitchAsync(2, [ssh, wsl, fixture.Target("windows", local: true)]);
        Assert.Equal("failed", result.State);
        Assert.Equal(new[] { "wsl:restore", "ssh:restore" }, fixture.Events.Where(value => value.EndsWith(":restore")));
        Assert.Equal(1, ssh.Active);
        Assert.Equal(1, wsl.Active);
        Assert.DoesNotContain("windows:apply", fixture.Events);
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
        public List<string> Events { get; } = [];
        public Fixture()
        {
            Paths = new WindowsPaths(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
            Coordinator = new(Paths);
        }
        public FakeTarget Target(string id, bool local = false) => new(id, local, Events, Coordinator.JournalPath);
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }

    private sealed class FakeTarget(string id, bool isLocal, List<string> events, string journalPath) : IAccountTarget
    {
        public string Id => id;
        public string DisplayName => id;
        public string ConfigurationFingerprint { get; set; } = "endpoint";
        public bool IsLocal => isLocal;
        public int Active { get; set; } = 1;
        public string? FailAt { get; set; }
        public bool FailRestore { get; set; }
        public Action? AfterApply { get; set; }
        public Task<string> PrepareAsync(int profileId, CancellationToken cancellationToken)
        {
            events.Add(id + ":prepare");
            if (FailAt == "prepare") throw new IOException("preflight failure");
            return Task.FromResult(Active.ToString());
        }
        public Task ApplyAsync(int profileId, CancellationToken cancellationToken)
        {
            var journal = JsonSerializer.Deserialize<SwitchJournal>(File.ReadAllText(journalPath));
            Assert.True(journal!.Checkpoints.Single(checkpoint => checkpoint.Id == id).Attempted);
            events.Add(id + ":apply");
            Active = profileId;
            AfterApply?.Invoke();
            if (FailAt == "apply") throw new IOException("connection lost after write: test-access-secret");
            return Task.CompletedTask;
        }
        public Task VerifyAsync(int profileId, CancellationToken cancellationToken)
        {
            events.Add(id + ":verify");
            if (FailAt == "verify") throw new IOException("verification failure");
            Assert.Equal(profileId, Active);
            return Task.CompletedTask;
        }
        public Task RestoreAsync(string checkpoint, CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.IsCancellationRequested);
            events.Add(id + ":restore");
            if (FailRestore) throw new IOException("device offline: test-access-secret");
            Active = int.Parse(checkpoint);
            return Task.CompletedTask;
        }
        public Task ReleaseAsync(string checkpoint, CancellationToken cancellationToken)
        {
            events.Add(id + ":release");
            return Task.CompletedTask;
        }
    }
}
