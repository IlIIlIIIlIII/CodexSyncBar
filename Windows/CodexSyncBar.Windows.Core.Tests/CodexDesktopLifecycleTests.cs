using System.Text.Json;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class CodexDesktopLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-desktop-" + Guid.NewGuid().ToString("N"));
    private readonly AuthStore _store;
    private readonly LocalSwitchService _service;
    private readonly FakeDesktop _desktop;

    public CodexDesktopLifecycleTests()
    {
        var paths = new WindowsPaths(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
        paths.EnsureDirectories();
        _store = new AuthStore(paths);
        for (var id = 1; id <= 2; id++)
        {
            var source = Path.Combine(paths.LoginSessionsDirectory, $"{id}.json");
            WindowsPathSafety.WritePrivateBytes(source, JsonSerializer.SerializeToUtf8Bytes(new CodexAuthFile
            {
                AuthMode = "chatgpt",
                Tokens = new CodexTokens { AccountId = $"account-{id}", AccessToken = "access", RefreshToken = "refresh", IdToken = "id" },
            }));
            _store.ImportAuth(source, id);
        }
        _store.SwitchActive(1);
        _desktop = new FakeDesktop(_store);
        _service = new LocalSwitchService(_store, paths, () => _desktop);
    }

    [Fact]
    public async Task SwitchClosesDesktopBeforeAuthChangeAndStartsItWithNewAccount()
    {
        await _service.SwitchAsync(2);
        Assert.Equal(["stop:account-1", "start:account-2"], _desktop.Events);
    }

    [Fact]
    public async Task FailedLaunchRestoresAuthAndRelaunchesPreviousAccount()
    {
        _desktop.FailNextStart = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SwitchAsync(2));
        Assert.Equal("account-1", _store.ReadActiveAccountId());
        Assert.Equal(["stop:account-1", "start:account-2", "stop:account-2", "start:account-1"], _desktop.Events);
    }

    [Fact]
    public async Task RestoreAlsoRestartsDesktop()
    {
        var previous = _store.ReadActiveAuth();
        _store.SwitchActive(2);
        await _service.RestoreAsync(previous);
        Assert.Equal(["stop:account-2", "start:account-1"], _desktop.Events);
    }

    [Fact]
    public async Task CancelledSwitchDoesNotCloseAppOrChangeAccount()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.SwitchAsync(2, new CancellationToken(true)));
        Assert.Empty(_desktop.Events);
        Assert.Equal("account-1", _store.ReadActiveAccountId());
    }

    [Fact]
    public async Task CancellationAfterClosingAppStillRestartsPreviousAccount()
    {
        using var cancellation = new CancellationTokenSource();
        _desktop.AfterStop = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.SwitchAsync(2, cancellation.Token));
        Assert.Equal("account-1", _store.ReadActiveAccountId());
        Assert.Equal(["stop:account-1", "stop:account-1", "start:account-1"], _desktop.Events);
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.924.1866.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.900.0.0_arm64__2p2nqsd0c76g0\app\Codex.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.ChatGPT_1_x64__2p2nqsd0c76g0\app\ChatGPT.exe", false)]
    [InlineData(@"C:\Users\Alice\AppData\Local\OpenAI\Codex\bin\version\codex.exe", false)]
    [InlineData(@"C:\Tools\Codex.exe", false)]
    public void OnlyCodexDesktopPackageIsSelected(string path, bool expected) =>
        Assert.Equal(expected, CodexDesktopLifecycle.IsDesktopExecutable(path));

    private sealed class FakeDesktop(AuthStore store) : ICodexDesktopSession
    {
        public List<string> Events { get; } = [];
        public bool FailNextStart { get; set; }
        public Action? AfterStop { get; set; }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            Events.Add("stop:" + store.ReadActiveAccountId());
            AfterStop?.Invoke();
            return Task.CompletedTask;
        }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Events.Add("start:" + store.ReadActiveAccountId());
            if (FailNextStart) { FailNextStart = false; throw new InvalidOperationException("launch failed"); }
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
