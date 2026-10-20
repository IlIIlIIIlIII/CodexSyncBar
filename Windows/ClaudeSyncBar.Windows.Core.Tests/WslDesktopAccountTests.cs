using System.Text.Json;
using System.Diagnostics;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Windows.Core.Tests;

public sealed class WslDesktopAccountTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-wsl-desktop-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("app.exe", null, true)]
    [InlineData("app.exe --user-data-dir=C:\\WSL", null, false)]
    [InlineData("app.exe --user-data-dir=C:\\WSL", "C:\\WSL", true)]
    [InlineData("app.exe --user-data-dir C:\\WSL", "C:\\WSL", true)]
    [InlineData("app.exe --user-data-dir=C:\\WSL-other", "C:\\WSL", false)]
    [InlineData("app.exe --type=renderer --user-data-dir=C:\\WSL", "C:\\WSL", false)]
    [InlineData("app.exe --user-data-dir=C:\\WSL --user-data-dir=C:\\other", "C:\\WSL", false)]
    public void DesktopInstancesAreScopedToTheirOwnProfile(string commandLine, string? directory, bool expected) =>
        Assert.Equal(expected, ClaudeDesktopLifecycle.MatchesProfile(ClaudeProcessScope.SplitCommandLine(commandLine), directory));

    [WindowsFact]
    public async Task BothAuthFilesSwitchAfterBothBackendsStopAndCanBeRestored()
    {
        var (paths, auth, profile, runner, desktop) = Setup();
        var target = new WslDesktopAccountTarget(paths, auth, profile, runner, _ => desktop);
        var checkpoint = await target.PrepareAsync(2, default);
        try
        {
            await target.ApplyAsync(2, default);
            await target.VerifyAsync(2, default);
            Assert.Equal(["desktop-stop", "linux-stop", "linux-write", "desktop-start"], runner.Events);
            Assert.Empty(JsonSerializer.Deserialize<ClaudeAuthFile>(runner.Auth!)!.Tokens.RefreshToken!);
            Assert.Equal("account-2", JsonSerializer.Deserialize<ClaudeAuthFile>(File.ReadAllText(profile.WindowsAuth))!.Tokens.AccountId);
            await target.RestoreAsync(checkpoint, default);
            Assert.Equal("account-1", JsonSerializer.Deserialize<ClaudeAuthFile>(runner.Auth!)!.Tokens.AccountId);
            Assert.Equal("account-1", JsonSerializer.Deserialize<ClaudeAuthFile>(File.ReadAllText(profile.WindowsAuth))!.Tokens.AccountId);
        }
        finally { await target.ReleaseAsync(checkpoint, default); }
    }

    [WindowsFact]
    public async Task RelaunchFailureRollsBackBothHomesThroughCoordinator()
    {
        var (paths, auth, profile, runner, desktop) = Setup();
        desktop.FailNextStart = true;
        var target = new WslDesktopAccountTarget(paths, auth, profile, runner, _ => desktop);
        var operation = await new AccountSwitchCoordinator(paths).SwitchAsync(2, [target]);
        Assert.Equal("failed", operation.State);
        Assert.Equal("account-1", JsonSerializer.Deserialize<ClaudeAuthFile>(runner.Auth!)!.Tokens.AccountId);
        Assert.Equal("account-1", JsonSerializer.Deserialize<ClaudeAuthFile>(File.ReadAllText(profile.WindowsAuth))!.Tokens.AccountId);
        Assert.Equal(2, runner.Events.Count(e => e == "desktop-start"));
    }

    [WindowsFact]
    public async Task ClosedAppStillGetsNewAuthWithoutOpeningAWindow()
    {
        var (paths, auth, profile, runner, desktop) = Setup();
        desktop.WasRunning = false;
        var target = new WslDesktopAccountTarget(paths, auth, profile, runner, _ => desktop);
        var operation = await new AccountSwitchCoordinator(paths).SwitchAsync(2, [target]);
        Assert.Equal("completed", operation.State);
        Assert.DoesNotContain("desktop-start", runner.Events);
        Assert.Equal("account-2", JsonSerializer.Deserialize<ClaudeAuthFile>(runner.Auth!)!.Tokens.AccountId);
    }

    [WindowsFact]
    public async Task MaintenanceUpdatesOnlyTheMatchingActiveAccount()
    {
        var (paths, auth, profile, runner, desktop) = Setup();
        var target = new WslDesktopAccountTarget(paths, auth, profile, runner, _ => desktop);
        await target.SyncActiveAuthAsync(2, default);
        Assert.Empty(runner.Events);
        await target.SyncActiveAuthAsync(1, default);
        Assert.Equal(["linux-write"], runner.Events);
        Assert.Empty(JsonSerializer.Deserialize<ClaudeAuthFile>(runner.Auth!)!.Tokens.RefreshToken!);
    }

    [WindowsFact]
    public void DiscoversSeparateHomeFromTheInstalledLauncher()
    {
        var paths = new WindowsPaths(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
        var root = Path.Combine(paths.Home, @"AppData\Local\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Local\Codex-WSL");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Start-Codex-WSL.ps1"),
            """$env:CODEX_CLI_PATH = '\\wsl.localhost\Ubuntu-26.04\home\alice\.codex-wsl-desktop\runtime\codex-wsl-app'""");
        var profile = WslDesktopProfile.Discover(paths)!;
        Assert.Equal("Ubuntu-26.04", profile.Distribution);
        Assert.Equal("alice", profile.Username);
        Assert.Equal("/home/alice/.codex-wsl-desktop", profile.ClaudeHome);
    }

    // Explicit live opt-in: bring only the WSL desktop to the current Windows account.
    [WslDesktopLiveFact]
    public async Task InstalledWslDesktopSwitchesToCurrentWindowsAccount()
    {
        var paths = new WindowsPaths();
        var auth = new AuthStore(paths);
        var profile = WslDesktopProfile.Discover(paths)!;
        var accounts = new ConfigurationStore(paths).LoadOrCreate().Accounts;
        var account = accounts
            .First(a => auth.ProfileArtifactExists(a.Id) && auth.ReadCredentials(a.Id).AccountId == auth.ReadActiveAccountId());
        var alternative = accounts.First(a => !a.NeedsLogin && !a.IsPending && a.Id != account.Id);
        using var mutation = await ControllerMutationLock.AcquireAsync(paths, TimeSpan.FromSeconds(30));
        var before = Process.GetProcessesByName("ChatGPT").Select(p => { using (p) return p.Id; }).ToHashSet();
        var target = new WslDesktopAccountTarget(paths, auth, profile);
        var checkpoint = await target.PrepareAsync(account.Id, default);
        try
        {
            await target.ApplyAsync(alternative.Id, default);
            await target.VerifyAsync(alternative.Id, default);
            Assert.Equal(auth.ReadCredentials(account.Id).AccountId, auth.ReadActiveAccountId());
            await target.ApplyAsync(account.Id, default);
            await target.VerifyAsync(account.Id, default);
            using var running = ClaudeDesktopLifecycle.Capture(paths, profile.UserData, profile.Launcher);
            Assert.True(running.WasRunning);
            var after = Process.GetProcessesByName("ChatGPT").Select(p => { using (p) return p.Id; }).ToHashSet();
            Assert.True(after.Except(before).Any());
            Assert.True(after.Intersect(before).Any());
        }
        catch { await target.RestoreAsync(checkpoint, default); throw; }
        finally { await target.ReleaseAsync(checkpoint, default); }
    }

    private (WindowsPaths, AuthStore, WslDesktopProfile, FakeRunner, FakeDesktop) Setup()
    {
        var paths = new WindowsPaths(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
        paths.EnsureDirectories();
        var auth = new AuthStore(paths);
        string? first = null;
        for (var id = 1; id <= 2; id++)
        {
            var json = JsonSerializer.Serialize(new ClaudeAuthFile { AuthMode = "chatgpt",
                Tokens = new() { AccountId = "account-" + id, AccessToken = "access", IdToken = "id", RefreshToken = "refresh" } });
            first ??= json;
            var source = Path.Combine(paths.LoginSessionsDirectory, id + ".json");
            WindowsPathSafety.WritePrivateBytes(source, System.Text.Encoding.UTF8.GetBytes(json));
            auth.ImportAuth(source, id);
        }
        var profile = new WslDesktopProfile(Path.Combine(_root, "desktop"), "Ubuntu", "test", "/home/test/.codex-wsl-desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(profile.WindowsAuth)!);
        File.WriteAllText(profile.WindowsAuth, first);
        var runner = new FakeRunner { Auth = first };
        return (paths, auth, profile, runner, new FakeDesktop(runner));
    }

    private sealed class FakeRunner : IWslCommandRunner
    {
        public string? Auth;
        public List<string> Events { get; } = [];
        public Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string? input, CancellationToken cancellationToken)
        {
            switch (arguments[^1])
            {
                case "stop": Events.Add("linux-stop"); break;
                case "write": Events.Add("linux-write"); Auth = JsonSerializer.Deserialize<string?>(input!); break;
                case "read": return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(Auth), ""));
            }
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }

    private sealed class FakeDesktop(FakeRunner runner) : IClaudeDesktopSession
    {
        public bool WasRunning { get; set; } = true;
        public bool FailNextStart;
        public Task StopAsync(CancellationToken cancellationToken) { runner.Events.Add("desktop-stop"); return Task.CompletedTask; }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!WasRunning) return Task.CompletedTask;
            runner.Events.Add("desktop-start");
            if (FailNextStart) { FailNextStart = false; throw new InvalidOperationException("test launch failure"); }
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

public sealed class WslDesktopLiveFactAttribute : WindowsFactAttribute
{
    public WslDesktopLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SYNCBAR_TEST_WSL_DESKTOP") != "1")
            Skip = "Explicit opt-in only: restarts WSL desktop and synchronizes its account.";
    }
}
