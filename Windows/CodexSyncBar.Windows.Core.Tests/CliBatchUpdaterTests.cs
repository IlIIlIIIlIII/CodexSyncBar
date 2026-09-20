using System.Collections.Concurrent;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public class CliBatchUpdaterTests
{
    [Fact]
    public async Task UpdatesOverlapAndOneFailureDoesNotCancelOtherDevices()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var events = new ConcurrentQueue<CliDeviceUpdate>();
        var progress = new CallbackProgress(value =>
        {
            events.Enqueue(value);
            if (value.DeviceId == "bad" && value.State == CliDeviceUpdateState.Failed) failureReported.TrySetResult();
        });
        async Task<CliUpdateResult> CompleteAsync(string restart, CancellationToken token)
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
            await release.Task.WaitAsync(token);
            return new("1.0.0", "2.0.0", "npm", restart);
        }
        var targets = new[]
        {
            new CliUpdateTarget("windows", "PC", new FakeService(token => CompleteAsync("reconnected", token))),
            new CliUpdateTarget("ssh:ml", "ml", new FakeService(token => CompleteAsync("reconnect-pending", token))),
            new CliUpdateTarget("bad", "offline", new FakeService(_ => throw new IOException("offline"))),
        };
        var task = new CliBatchUpdater().RunAsync(targets, progress);
        try
        {
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await failureReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(task.IsCompleted);
            Assert.Equal(2, started);
        }
        finally { release.TrySetResult(); }
        var results = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CliDeviceUpdateState.Completed, results[0].State);
        Assert.Equal(CliDeviceUpdateState.ReconnectPending, results[1].State);
        Assert.Equal(CliDeviceUpdateState.Failed, results[2].State);
        Assert.Equal("2.0.0", results[0].After);
        Assert.Contains(events, value => value.DeviceId == "ssh:ml" && value.State == CliDeviceUpdateState.Updating);
    }

    [Fact]
    public async Task UntrustedAndUnsupportedDevicesAreSkippedWithoutUpdating()
    {
        var service = new FakeService(_ => throw new Exception("Must not run"));
        var unsupported = new FakeService(_ => throw new Exception("Must not run")) { Supported = false };
        var result = await new CliBatchUpdater().RunAsync([
            new("untrusted", "new host", service, _ => Task.FromResult<string?>("호스트 키 확인 필요")),
            new("unsupported", "custom CLI", unsupported),
        ]);
        Assert.All(result, state => Assert.Equal(CliDeviceUpdateState.Skipped, state.State));
        Assert.Equal(0, service.Inspections);
        Assert.Equal(0, service.Updates);
        Assert.Equal(0, unsupported.Updates);
    }

    [Fact]
    public void DuplicateDevicesAreRejectedBeforeStarting()
    {
        var service = new FakeService(_ => Task.FromResult(new CliUpdateResult("1", "2", "npm", "not-running")));
        Assert.Throws<ArgumentException>(() => { _ = new CliBatchUpdater().RunAsync([
            new("ssh:ml", "ml", service), new("SSH:ML", "duplicate", service),
        ]); });
        Assert.Equal(0, service.Updates);
    }

    private sealed class FakeService(Func<CancellationToken, Task<CliUpdateResult>> update) : ICliManagementService
    {
        public bool Supported { get; init; } = true;
        public int Inspections;
        public int Updates;
        public Task<CliInstallation> InspectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Inspections);
            return Task.FromResult(new CliInstallation("1.0.0", "/bin/codex", "npm", Supported, "지원하지 않는 설치"));
        }
        public Task<CliUpdateResult> UpdateAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Updates);
            return update(cancellationToken);
        }
    }

    private sealed class CallbackProgress(Action<CliDeviceUpdate> report) : IProgress<CliDeviceUpdate>
    {
        public void Report(CliDeviceUpdate value) => report(value);
    }
}
