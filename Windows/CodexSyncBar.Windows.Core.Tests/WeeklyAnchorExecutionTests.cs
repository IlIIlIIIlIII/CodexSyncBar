using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class WeeklyAnchorExecutionTests
{
    [Fact]
    public async Task ResetSendsOnceAndPersistsAcrossRestart()
    {
        using var f = new Fixture();
        var calls = 0;
        var coordinator = f.Coordinator((_, _) => { calls++; return Task.FromResult("확인"); });
        await coordinator.SetEnabledAsync(1, true);
        f.Record(new() { NextResetAt = f.Now.AddMinutes(-1) });
        Assert.True(await coordinator.EvaluateAsync(f.Snapshot()));
        Assert.False(await f.Coordinator((_, _) => { calls++; return Task.FromResult("확인"); }).EvaluateAsync(f.Snapshot()));
        Assert.Equal(1, calls);
        Assert.Equal(f.Now.AddMinutes(-1), coordinator.Load().Records[1].LastHandledResetAt);
    }

    [Fact]
    public async Task DuplicateRefreshAndManualClickCannotSendConcurrently()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = f.Coordinator((_, _) => { entered.SetResult(); return release.Task; });
        await coordinator.SetEnabledAsync(1, true);
        var first = coordinator.EvaluateAsync(f.Snapshot());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.IsRunning(1));
        Assert.False(await coordinator.EvaluateAsync(f.Snapshot(), manual: true));
        Assert.Equal(f.Now, coordinator.Load().Records[1].LastAttemptAt);
        release.SetResult("확인");
        Assert.True(await first);
        Assert.False(coordinator.IsRunning(1));
    }

    [Fact]
    public async Task FailureSurvivesRestartAndRetriesAfterThirtyMinutes()
    {
        using var f = new Fixture();
        var coordinator = f.Coordinator((_, _) => throw new IOException("offline"));
        await coordinator.SetEnabledAsync(1, true);
        await Assert.ThrowsAsync<IOException>(() => coordinator.EvaluateAsync(f.Snapshot()));
        Assert.Equal("offline", coordinator.Load().Records[1].LastError);
        var restarted = f.Coordinator((_, _) => Task.FromResult("확인"));
        Assert.False(await restarted.EvaluateAsync(f.Snapshot()));
        f.Now = f.Now.AddMinutes(31);
        Assert.True(await restarted.EvaluateAsync(f.Snapshot()));
        Assert.Null(restarted.Load().Records[1].LastError);
    }

    [Fact]
    public async Task SeparateControllerInstancesUseOneDurableAttempt()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.Coordinator((_, _) => { entered.SetResult(); return release.Task; });
        await first.SetEnabledAsync(1, true);
        var running = first.EvaluateAsync(f.Snapshot());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var other = f.Coordinator((_, _) => throw new Exception("duplicate send"));
        var queued = other.EvaluateAsync(f.Snapshot());
        release.SetResult("확인");
        Assert.True(await running);
        Assert.False(await queued);
    }

    [Fact]
    public async Task MissingResponseIsNotSuccessful()
    {
        using var f = new Fixture();
        var service = new WeeklyAnchorService(f.Paths, new AuthStore(f.Paths))
        {
            ExecutableOverride = "fake-cli",
            RunOverride = (_, _, _, _) => Task.FromResult(new ProcessResult(0, "diagnostics only", "")),
        };
        var credentials = new ProfileCredentials(1, "access", null, "refresh", "account", "test@example.com", null, "unused", false);
        await Assert.ThrowsAsync<CodexSyncBarException>(() => service.SendOnceAsync(credentials, default));
        Assert.Empty(Directory.GetDirectories(f.Paths.ExternalRuntimeDirectory, "CodexSyncBarWeeklyAnchor-*"));
    }

    [Fact]
    public async Task DisabledStaleMissingAndAlreadyUsedWindowsDoNotSend()
    {
        using var f = new Fixture();
        var coordinator = f.Coordinator((_, _) => throw new Exception("must not send"));
        Assert.False(await coordinator.EvaluateAsync(f.Snapshot()));
        await coordinator.SetEnabledAsync(1, true);
        Assert.False(await coordinator.EvaluateAsync(f.Snapshot() with { UpdatedAt = f.Now.AddMinutes(-6) }));
        Assert.False(await coordinator.EvaluateAsync(f.Snapshot() with { Weekly = null }));
        f.Record(new() { NextResetAt = f.Now.AddMinutes(-1) });
        Assert.False(await coordinator.EvaluateAsync(f.Snapshot() with { Weekly = new(5, f.Now.AddDays(7), 604800) }));
        Assert.NotNull(coordinator.Load().Records[1].LastHandledResetAt);
        var store = new ConfigurationStore(f.Paths);
        var configuration = store.LoadOrCreate();
        store.MarkAccountNeedsLogin(configuration, 1);
        Assert.False(await coordinator.EvaluateAsync(f.Snapshot(), manual: true));
    }

    [Fact]
    public async Task DriftNeedsTwoFreshObservationsAndUserActivityAdoptsNewSchedule()
    {
        using var f = new Fixture();
        var calls = 0;
        var coordinator = f.Coordinator((_, _) => { calls++; return Task.FromResult("확인"); });
        await coordinator.SetEnabledAsync(1, true);
        f.Record(new() { NextResetAt = f.Now.AddDays(2) });
        Assert.False(await coordinator.EvaluateAsync(f.Snapshot()));
        Assert.Equal(1, coordinator.Load().Records[1].ResetDriftObservationCount);
        f.Now = f.Now.AddMinutes(5);
        Assert.True(await coordinator.EvaluateAsync(f.Snapshot()));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SenderUsesIsolatedPrivateHomeMacPromptAndCleansUp(bool fail)
    {
        using var f = new Fixture();
        string? runtime = null;
        var credentials = new ProfileCredentials(1, "synthetic-access-secret", null, "synthetic-refresh-secret", "synthetic-account", "test@example.com", null, "unused", false);
        var service = new WeeklyAnchorService(f.Paths, new AuthStore(f.Paths))
        {
            ExecutableOverride = "synthetic-cli",
            RunOverride = (_, args, env, _) =>
            {
                var home = env["CODEX_HOME"]!;
                runtime = Path.GetDirectoryName(home)!;
                Assert.StartsWith(f.Paths.ExternalRuntimeDirectory, home);
                Assert.Equal(credentials.AccessToken, env["CODEX_SYNCBAR_ACCESS_TOKEN"]);
                Assert.Null(env["CODEX_API_KEY"]);
                Assert.DoesNotContain(credentials.AccessToken, string.Join(' ', args));
                Assert.DoesNotContain(credentials.RefreshToken, string.Join(' ', env.Values));
                Assert.Contains("--ignore-user-config", args);
                Assert.Contains("--ignore-rules", args);
                Assert.Contains("--ephemeral", args);
                Assert.Contains("read-only", args);
                Assert.Contains("gpt-5.6-luna", args);
                Assert.EndsWith("‘확인’만 답해주세요.", args[^1]);
                Assert.False(File.Exists(Path.Combine(home, "auth.json")));
                var response = args[Array.IndexOf(args, "--output-last-message") + 1];
                File.WriteAllText(response, "확인");
                WindowsPathSafety.EnsurePrivateFile(response, "response", 1024);
                return Task.FromResult(new ProcessResult(fail ? 1 : 0, fail ? credentials.AccessToken : "", ""));
            },
        };
        if (fail)
        {
            var error = await Assert.ThrowsAsync<CodexSyncBarException>(() => service.SendOnceAsync(credentials, default));
            Assert.DoesNotContain(credentials.AccessToken, error.Message);
        }
        else Assert.Equal("확인", await service.SendOnceAsync(credentials, default));
        Assert.NotNull(runtime);
        Assert.False(Directory.Exists(runtime));
        Assert.False(File.Exists(f.Paths.ActiveAuthFile));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-weekly-" + Guid.NewGuid().ToString("N"));
        public WindowsPaths Paths { get; }
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public Fixture()
        {
            Paths = new(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
            var store = new ConfigurationStore(Paths);
            store.UpdateAccountEmail(store.LoadOrCreate(), 1, "test@example.com");
        }
        public WeeklyAnchorCoordinator Coordinator(Func<int, CancellationToken, Task<string>> send) => new(Paths, send, () => Now);
        public UsageSnapshot Snapshot() => new(1, "test@example.com", "Pro", null, new(0, Now.AddDays(7), 604800), null, false, null, [], Now);
        public void Record(WeeklyAnchorRecord record)
        {
            var store = new WeeklyAnchorStore(Paths); var state = store.Load(); state.Records[1] = record; store.Save(state);
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
