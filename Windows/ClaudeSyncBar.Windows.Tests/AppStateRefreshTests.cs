using System.Text.Json;
using ClaudeSyncBar.App.Model;
using ClaudeSyncBar.Ubuntu.Backend;

namespace ClaudeSyncBar.Windows.Tests;

public sealed class AppStateRefreshTests
{
    // The timer's look reads the state just before the refresh starts its job and answers after it.
    // The refresh used to skip its own look then and show nothing running until the next tick:
    // the quick view's spinner went away and the smart switch came back for a moment.
    [Fact]
    public async Task RefreshShowsNothingIdleUntilALookAfterTheJobStarted()
    {
        var service = new HeldService();
        var state = new AppState(new ServiceClient(service));
        var seen = new List<(bool Requested, bool Busy)>();
        state.Changed += () => { lock (seen) seen.Add((state.RefreshRequested, state.Busy)); };

        state.PollIfIdle();
        await service.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var refresh = state.RefreshAsync();
        await service.RefreshCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Before the fix the refresh was over by now, while the older look was still out.
        await Task.WhenAny(refresh, Task.Delay(300));
        service.ReleaseFirst();
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(state.Busy);
        Assert.False(state.RefreshRequested);
        lock (seen)
        {
            var afterClick = seen.SkipWhile(change => !change.Requested).ToList();
            Assert.NotEmpty(afterClick);
            Assert.DoesNotContain(afterClick, change => !change.Requested && !change.Busy);
        }
    }

    private sealed class HeldService : IManagementService
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _snapshots;
        private volatile bool _busy;

        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RefreshCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ShutdownRequested => false;

        public void ReleaseFirst() => _release.TrySetResult();

        public async Task<object?> DispatchAsync(string method, JsonElement parameters, CancellationToken token)
        {
            switch (method)
            {
                case "snapshot":
                    var busy = _busy;
                    if (Interlocked.Increment(ref _snapshots) == 1)
                    {
                        FirstRead.TrySetResult();
                        await _release.Task.WaitAsync(token);
                    }

                    return new { isBusy = busy, accounts = Array.Empty<object>(), operation = busy ? new { id = "job", kind = "refresh", state = "running" } : null };
                case "refresh":
                    _busy = true;
                    RefreshCalled.TrySetResult();
                    return new { id = "job", kind = "refresh", state = "queued" };
                default:
                    throw new RpcException("busy", "진행 중인 작업을 먼저 완료해 주세요.");
            }
        }

        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
