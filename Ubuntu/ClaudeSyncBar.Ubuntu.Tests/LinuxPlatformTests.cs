using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ClaudeSyncBar.Windows.Core;
using Contracts = ClaudeSyncBar.Windows.Core.Tests.TestAuth;
using Xunit;

namespace ClaudeSyncBar.Ubuntu.Tests;

[CollectionDefinition("Linux platform isolation", DisableParallelization = true)]
public sealed class LinuxPlatformIsolationCollection;

[Collection("Linux platform isolation")]
public sealed class LinuxPlatformTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-linux-platform-" + Guid.NewGuid().ToString("N"));
    private WindowsPaths Paths => new(Path.Combine(_root, "home"), Path.Combine(_root, "data"));

    [Fact]
    public void OpenSshIsLinuxsOwnEvenWhenWslPutsWindowsSshFirstOnPath()
    {
        // Under WSL, PATH also lists Windows folders, and those can come first.
        var windows = Path.Combine(_root, "Windows", "System32", "OpenSSH");
        var linux = Path.Combine(_root, "usr", "bin");
        Directory.CreateDirectory(windows);
        Directory.CreateDirectory(linux);
        foreach (var name in new[] { "ssh.exe", "scp.exe" }) File.WriteAllText(Path.Combine(windows, name), "windows program");
        foreach (var name in new[] { "ssh", "scp" }) File.WriteAllText(Path.Combine(linux, name), "linux program");
        var previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", windows + Path.PathSeparator + linux);
            Assert.Equal(Path.Combine(linux, "ssh"), SshDeviceService.FindExecutable("ssh"));
            Assert.Equal(Path.Combine(linux, "scp"), SshDeviceService.FindExecutable("scp"));
            // With only the Windows program reachable, nothing is used rather than it.
            Environment.SetEnvironmentVariable("PATH", windows);
            Assert.Throws<ClaudeSyncBarException>(() => SshDeviceService.FindExecutable("ssh"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previous);
        }
    }

    [Fact]
    public void ExplicitFixtureHomeNeverInheritsProductionOverrides()
    {
        var originalHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var originalRoot = Environment.GetEnvironmentVariable("CLAUDE_SYNCBAR_STATE_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", "/forbidden/production/claude");
            Environment.SetEnvironmentVariable("CLAUDE_SYNCBAR_STATE_ROOT", "/forbidden/production/state");
            var paths = Paths;
            paths.EnsureDirectories();
            Assert.Equal(Path.Combine(_root, "home", ".claude"), paths.ClaudeHome);
            // Without CLAUDE_CONFIG_DIR, Claude Code keeps its global config in the home directory.
            Assert.Equal(Path.Combine(_root, "home", ".claude.json"), paths.ClaudeConfigFile);
            Assert.Equal(Path.Combine(_root, "home", ".claude", ".credentials.json"), paths.ActiveAuthFile);
            Assert.Equal(Path.Combine(_root, "data", "claude-syncbar"), paths.StateRoot);
            Assert.True(paths.IsIsolated);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", originalHome);
            Environment.SetEnvironmentVariable("CLAUDE_SYNCBAR_STATE_ROOT", originalRoot);
        }
    }

    [Fact]
    public void CredentialsArePrivateBeforeWritingAndUnsafeInputsAreRejected()
    {
        var paths = Paths;
        paths.EnsureDirectories();
        var file = Path.Combine(paths.StateRoot, "private");
        WindowsPathSafety.WritePrivateBytes(file, "fixture"u8.ToArray());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.Equal("fixture", Encoding.UTF8.GetString(WindowsPathSafety.ReadPrivateFile(file, "test", 100)));
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        Assert.Throws<ClaudeSyncBarException>(() => WindowsPathSafety.ReadPrivateFile(file, "test", 100));
        var link = Path.Combine(paths.StateRoot, "link");
        File.CreateSymbolicLink(link, file);
        Assert.Throws<ClaudeSyncBarException>(() => WindowsPathSafety.EnsureFile(link, "test"));
        var directoryLink = Path.Combine(paths.StateRoot, "directory-link");
        Directory.CreateSymbolicLink(directoryLink, paths.ProfilesDirectory);
        Assert.Throws<ClaudeSyncBarException>(() => WindowsPathSafety.WritePrivateBytes(Path.Combine(directoryLink, "outside"), [1]));
    }

    [Fact]
    public void VaultBackupsAreEncryptedAndBoundToPurposeWithTamperDetection()
    {
        using var fixtureKey = LinuxSecretKeyProvider.OverrideForTests(() => Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var paths = Paths;
        var store = new WindowsSecretStore(paths);
        store.Save("fixture-refresh-token", "rollback-one");
        Assert.Equal("fixture-refresh-token", store.Read("rollback-one"));
        var file = Path.Combine(paths.StateRoot, "secrets", "rollback-one.bin");
        var encrypted = File.ReadAllBytes(file);
        Assert.DoesNotContain("fixture-refresh-token", Encoding.UTF8.GetString(encrypted));
        Assert.ThrowsAny<CryptographicException>(() => WindowsSecretStore.Unprotect(encrypted, "rollback-two"));
        encrypted[^1] ^= 0x40;
        Assert.ThrowsAny<CryptographicException>(() => WindowsSecretStore.Unprotect(encrypted, "rollback-one"));
        Assert.ThrowsAny<CryptographicException>(() => WindowsSecretStore.Unprotect("plaintext"u8.ToArray(), "rollback-one"));
        store.Delete("rollback-one");
        Assert.Null(store.Read("rollback-one"));
    }

    [Fact]
    public async Task RunningCliDetectionIsScopedToTheConfigDirectoryAndCredentialFile()
    {
        var paths = Paths;
        paths.EnsureDirectories();
        var service = new LocalSwitchService(new AuthStore(paths), paths);
        Assert.False(service.HasClaudeClientsRunning());

        // Sessions of another config directory, or hosted by Claude Desktop, never read this login.
        using var other = StartFixtureCli(Path.Combine(_root, "other-home"));
        using var hosted = StartFixtureCli(paths.ClaudeHome, ("CLAUDE_CODE_ENTRYPOINT", "claude-desktop"));
        using var tokenFromEnvironment = StartFixtureCli(paths.ClaudeHome, ("CLAUDE_CODE_OAUTH_TOKEN", "fixture"));
        try
        {
            await Task.Delay(150);
            Assert.False(service.HasClaudeClientsRunning());
            using var current = StartFixtureCli(paths.ClaudeHome);
            try
            {
                await Task.Delay(150);
                Assert.True(service.HasClaudeClientsRunning());
                var stopped = false;
                foreach (var observed in LinuxProcess.Enumerate())
                    using (observed)
                    {
                        if (observed.Id == other.Id) Assert.False(observed.UsesClaudeHome(paths));
                        if (observed.Id == hosted.Id) Assert.False(observed.ReadsCredentialFile);
                        if (observed.Id != current.Id) continue;
                        Assert.True(observed.IsClaudeCli);
                        Assert.True(observed.UsesClaudeHome(paths));
                        Assert.True(observed.ReadsCredentialFile);
                        await observed.StopAsync(default);
                        stopped = true;
                    }
                Assert.True(stopped);
                await current.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(service.HasClaudeClientsRunning());
                Assert.False(other.HasExited);
                Assert.False(hosted.HasExited);
            }
            finally { StopFixture(current); }
        }
        finally { StopFixture(other); StopFixture(hosted); StopFixture(tokenFromEnvironment); }
    }

    [Theory]
    [InlineData("/home/user/.local/share/claude/versions/2.1.0", "claude", true)]
    [InlineData("/home/user/.config/Claude/claude-code/2.1.0/abc/claude", "/home/user/.config/Claude/claude-code/2.1.0/abc/claude", true)]
    [InlineData("/usr/bin/node", "node", false)]
    [InlineData("/usr/lib/claude-desktop/claude-desktop", "/usr/lib/claude-desktop/claude-desktop", false)]
    [InlineData("/usr/bin/python3", "claude-switch", false)]
    public void CliProcessesAreRecognizedByLauncherNameOrInstallLocation(string executable, string launcher, bool expected) =>
        Assert.Equal(expected, LinuxProcess.IsClaudeCliCommand(executable, [launcher, "--print"]));

    [Fact]
    public void NpmInstalledCliIsRecognizedByItsPackagePath() =>
        Assert.True(LinuxProcess.IsClaudeCliCommand("/usr/bin/node",
            ["node", "/usr/lib/node_modules/@anthropic-ai/claude-code/cli.js"]));

    [Fact]
    public async Task SwitchingWhileASessionRunsAsksForThatSessionToBeRestarted()
    {
        using var fixtureKey = LinuxSecretKeyProvider.OverrideForTests(() => Enumerable.Repeat((byte)29, 32).ToArray());
        var paths = Paths;
        paths.EnsureDirectories();
        var auth = new AuthStore(paths);
        for (var id = 1; id <= 2; id++)
        {
            var file = Path.Combine(paths.LoginSessionsDirectory, id + ".json");
            WindowsPathSafety.WritePrivateBytes(file, Contracts.Bytes(Contracts.Create("fixture-" + id, "access-" + id, "refresh-" + id)));
            auth.ImportAuth(file, id);
        }
        var service = new LocalSwitchService(auth, paths);
        await service.SwitchAsync(1);
        Assert.Null(service.ReconnectionDetail);
        using var session = StartFixtureCli(paths.ClaudeHome);
        try
        {
            await Task.Delay(150);
            await service.SwitchAsync(2);
            Assert.Equal("fixture-2", auth.ReadActiveAccountId());
            Assert.NotNull(service.ReconnectionDetail);
        }
        finally { StopFixture(session); }
        await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        service.RefreshClientStatus();
        Assert.Null(service.ReconnectionDetail);
    }

    [Fact]
    public async Task RemotePreviewStatusDoesNotCreateDirectoriesRecoverJournalsOrLaunchCli()
    {
        var paths = Paths;
        var script = Path.GetFullPath("../../Support/claude-switch", Path.GetDirectoryName(SourceFile())!);
        async Task<ProcessResult> ReadStatus() => await ProcessRunner.RunAsync("/bin/bash", [script, "__node", "status-readonly"],
            environment: new Dictionary<string, string>
            {
                ["HOME"] = paths.Home, ["CLAUDE_SWITCH_STATE_ROOT"] = paths.StateRoot,
                ["PATH"] = Path.Combine(_root, "bin") + ":/usr/bin:/bin",
            });
        var absent = await ReadStatus();
        Assert.Equal(0, absent.ExitCode);
        Assert.Contains("active=unknown", absent.StandardOutput);
        Assert.False(Directory.Exists(paths.StateRoot));
        Assert.False(Directory.Exists(paths.ClaudeHome));

        var login = Contracts.Create("fixture-status", "access", "");
        var auth = Contracts.Bytes(login);
        var credentials = "{\"mcpOAuth\":{},\"claudeAiOauth\":{\"accessToken\":\"access\",\"refreshToken\":\"\"}}"u8.ToArray();
        var config = "{\"numStartups\":3,\"oauthAccount\":{\"accountUuid\":\"fixture-status\"}}"u8.ToArray();
        Directory.CreateDirectory(paths.ClaudeHome, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        WindowsPathSafety.WritePrivateBytes(paths.ActiveAuthFile, credentials);
        WindowsPathSafety.WritePrivateBytes(paths.ClaudeConfigFile, config);
        var withoutSlots = await ReadStatus();
        Assert.Equal(0, withoutSlots.ExitCode);
        Assert.Contains("active=unknown", withoutSlots.StandardOutput);
        Assert.Contains("fingerprint=" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("fixture-status")))[..12], withoutSlots.StandardOutput);
        Assert.False(Directory.Exists(paths.StateRoot));
        Assert.Equal(credentials, File.ReadAllBytes(paths.ActiveAuthFile));
        Assert.Equal(config, File.ReadAllBytes(paths.ClaudeConfigFile));
        paths.EnsureDirectories();
        WindowsPathSafety.WritePrivateBytes(paths.ProfileAuthFile(1), auth);
        var journal = Path.Combine(paths.StateRoot, ".swap-profiles.fixture");
        Directory.CreateDirectory(journal, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(journal, "manifest"), "state=committed\npid=99999999\n");
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var cli = Path.Combine(bin, "claude");
        File.WriteAllText(cli, "#!/bin/sh\ntouch \"$HOME/cli-was-launched\"\n");
        File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var initialFiles = Directory.GetFileSystemEntries(paths.StateRoot, "*", SearchOption.AllDirectories).Order().ToArray();
        File.SetUnixFileMode(paths.StateRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        var before = File.GetUnixFileMode(paths.StateRoot);
        var status = await ReadStatus();
        Assert.Equal(0, status.ExitCode);
        Assert.Contains("active=1", status.StandardOutput);
        Assert.Equal(before, File.GetUnixFileMode(paths.StateRoot));
        Assert.Equal(initialFiles, Directory.GetFileSystemEntries(paths.StateRoot, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.Equal(credentials, File.ReadAllBytes(paths.ActiveAuthFile));
        Assert.Equal(config, File.ReadAllBytes(paths.ClaudeConfigFile));
        Assert.False(File.Exists(Path.Combine(paths.Home, "cli-was-launched")));
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public async Task FailedDesktopRestartRestoresEncryptedAccountAndActiveIdentity()
    {
        using var fixtureKey = LinuxSecretKeyProvider.OverrideForTests(() => Enumerable.Repeat((byte)31, 32).ToArray());
        var paths = Paths;
        paths.EnsureDirectories();
        var auth = new AuthStore(paths);
        for (var id = 1; id <= 2; id++)
        {
            var file = Path.Combine(paths.LoginSessionsDirectory, id + ".json");
            WindowsPathSafety.WritePrivateBytes(file, Contracts.Bytes(Contracts.Create("fixture-" + id, "access", "fixture-refresh")));
            auth.ImportAuth(file, id);
            Assert.DoesNotContain("fixture-refresh", File.ReadAllText(paths.ProfileAuthFile(id)));
        }
        auth.SwitchActive(1);
        // Claude Desktop is restarted only when its profile has to change hands.
        Directory.CreateDirectory(paths.DesktopUserDataDirectory);
        File.WriteAllText(Path.Combine(paths.DesktopUserDataDirectory, "config.json"), "{\"lastKnownAccountUuid\":\"fixture-1\"}");
        var desktop = new FailingDesktop(auth);
        var service = new LocalSwitchService(auth, paths, () => desktop);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchAsync(2));
        Assert.Equal("fixture-1", auth.ReadActiveAccountId());
        Assert.Equal(new[] { "stop:fixture-1", "start:fixture-2", "stop:fixture-2", "start:fixture-1" }, desktop.Events);
        Assert.Equal("fixture-2", auth.ReadCredentials(2).AccountId);
    }
    private sealed class FailingDesktop(AuthStore auth) : IClaudeDesktopSession
    {
        private bool _fail = true;
        public List<string> Events { get; } = [];
        public Task StopAsync(CancellationToken token) { Events.Add("stop:" + auth.ReadActiveAccountId()); return Task.CompletedTask; }
        public Task StartAsync(CancellationToken token)
        {
            Events.Add("start:" + auth.ReadActiveAccountId());
            if (_fail) { _fail = false; throw new InvalidOperationException("Fixture restart failure"); }
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }

    private static Process StartFixtureCli(string claudeHome, params (string Name, string Value)[] environment)
    {
        var start = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("exec -a claude /usr/bin/python3 -c 'import time; time.sleep(60)' --print");
        start.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
        start.Environment.Remove("CLAUDE_CODE_OAUTH_TOKEN");
        start.Environment["CLAUDE_CONFIG_DIR"] = claudeHome;
        foreach (var (name, value) in environment) start.Environment[name] = value;
        return Process.Start(start)!;
    }
    private static void StopFixture(Process process)
    {
        if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
