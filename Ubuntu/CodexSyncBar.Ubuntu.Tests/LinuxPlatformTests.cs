using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodexSyncBar.Windows.Core;
using Xunit;

namespace CodexSyncBar.Ubuntu.Tests;

[CollectionDefinition("Linux platform isolation", DisableParallelization = true)]
public sealed class LinuxPlatformIsolationCollection;

[Collection("Linux platform isolation")]
public sealed class LinuxPlatformTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-linux-platform-" + Guid.NewGuid().ToString("N"));
    private WindowsPaths Paths => new(Path.Combine(_root, "home"), Path.Combine(_root, "data"));

    [Fact]
    public void ExplicitFixtureHomeNeverInheritsProductionOverrides()
    {
        var originalHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var originalRoot = Environment.GetEnvironmentVariable("CODEX_SYNCBAR_STATE_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", "/forbidden/production/codex");
            Environment.SetEnvironmentVariable("CODEX_SYNCBAR_STATE_ROOT", "/forbidden/production/state");
            var paths = Paths;
            paths.EnsureDirectories();
            Assert.Equal(Path.Combine(_root, "home", ".codex"), paths.CodexHome);
            Assert.Equal(Path.Combine(_root, "data", "codex-syncbar"), paths.StateRoot);
            Assert.True(paths.IsIsolated);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", originalHome);
            Environment.SetEnvironmentVariable("CODEX_SYNCBAR_STATE_ROOT", originalRoot);
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
        Assert.Throws<CodexSyncBarException>(() => WindowsPathSafety.ReadPrivateFile(file, "test", 100));
        var link = Path.Combine(paths.StateRoot, "link");
        File.CreateSymbolicLink(link, file);
        Assert.Throws<CodexSyncBarException>(() => WindowsPathSafety.EnsureFile(link, "test"));
        var directoryLink = Path.Combine(paths.StateRoot, "directory-link");
        Directory.CreateSymbolicLink(directoryLink, paths.ProfilesDirectory);
        Assert.Throws<CodexSyncBarException>(() => WindowsPathSafety.WritePrivateBytes(Path.Combine(directoryLink, "outside"), [1]));
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
    public async Task ScopedTerminationLeavesOtherHomeAndTcpServersAlive()
    {
        var paths = Paths;
        paths.EnsureDirectories();
        using var current = StartFixtureServer(paths.CodexHome, "unix:///tmp/syncbar-fixture");
        using var other = StartFixtureServer(Path.Combine(_root, "other-home"), "unix:///tmp/syncbar-other");
        using var tcp = StartFixtureServer(paths.CodexHome, "tcp://127.0.0.1:49999");
        try
        {
            await Task.Delay(150);
            var stopped = false;
            foreach (var observed in LinuxProcess.Enumerate())
                using (observed)
                {
                    if (observed.Id != current.Id) continue;
                    Assert.True(observed.IsAppServer);
                    Assert.True(observed.UsesCodexHome(paths));
                    await observed.StopAsync(default);
                    stopped = true;
                }
            Assert.True(stopped);
            await current.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(other.HasExited);
            Assert.False(tcp.HasExited);
            foreach (var observed in LinuxProcess.Enumerate())
                using (observed)
                {
                    if (observed.Id == other.Id) Assert.False(observed.UsesCodexHome(paths));
                    if (observed.Id == tcp.Id) Assert.False(observed.IsAppServer);
                }
        }
        finally { StopFixture(current); StopFixture(other); StopFixture(tcp); }
    }

    [Fact]
    public void DesktopRecognitionDoesNotMatchGenericCliOrChatGPT()
    {
        Assert.True(CodexDesktopLifecycle.IsDesktopExecutable("/opt/codex/codex"));
        Assert.True(CodexDesktopLifecycle.IsDesktopExecutable("/usr/lib/chatgpt/ChatGPT"));
        Assert.False(CodexDesktopLifecycle.IsDesktopExecutable("/usr/bin/codex"));
        Assert.False(CodexDesktopLifecycle.IsDesktopExecutable("/opt/chatgpt/ChatGPT"));
        Assert.False(CodexDesktopLifecycle.IsDesktopExecutable("/opt/codex-other/codex"));
    }

    private static DesktopProcessEvidence Desktop(int id = 100, int parent = 1, ulong started = 100,
        string[]? arguments = null, Dictionary<string, string>? environment = null) =>
        new(id, parent, started, "/usr/lib/chatgpt/ChatGPT", arguments ?? ["/usr/lib/chatgpt/ChatGPT"],
            environment ?? [], true, false);
    private static DesktopProcessEvidence PackagedServer(string home, int id = 102, int parent = 100, ulong started = 102) =>
        new(id, parent, started, "/usr/lib/chatgpt/resources/codex", ["codex", "app-server"],
            new Dictionary<string, string> { ["HOME"] = home }, true, true);

    [Fact]
    public void FlattenedElectronChildrenAreNotDesktopRoots()
    {
        var root = Desktop();
        var renderer = Desktop(101, 100, 101, ["/usr/lib/chatgpt/ChatGPT --type=renderer --no-sandbox"]);
        var utility = Desktop(103, 1, 103, ["/usr/lib/chatgpt/ChatGPT --enable-logging --type=utility"]);
        var titleOnlyChild = Desktop(104, 100, 104);
        var processes = new[] { root, renderer, utility, titleOnlyChild };
        Assert.True(CodexDesktopLifecycle.IsDesktopRoot(root, processes));
        Assert.False(CodexDesktopLifecycle.IsDesktopRoot(renderer, processes));
        Assert.False(CodexDesktopLifecycle.IsDesktopRoot(utility, processes));
        Assert.False(CodexDesktopLifecycle.IsDesktopRoot(titleOnlyChild, processes));
    }

    [Fact]
    public void ErasedRootEnvironmentUsesVerifiedPackagedAppServerDescendant()
    {
        var root = Desktop();
        var server = PackagedServer(Paths.Home);
        Assert.Null(LinuxProcess.GetCodexHome(root.Environment));
        Assert.Equal(Paths.CodexHome, CodexDesktopLifecycle.ResolveCodexHome(root, [root, server]));
        var child = Desktop(101, 100, 101, ["/usr/lib/chatgpt/ChatGPT --type=utility"]);
        server = server with { ParentId = 101 };
        Assert.Equal(Paths.CodexHome, CodexDesktopLifecycle.ResolveCodexHome(root, [root, child, server]));
    }

    [Fact]
    public void DesktopScopeRejectsUnrelatedOrUnpackagedServersAndReusedParents()
    {
        var root = Desktop();
        var server = PackagedServer(Paths.Home);
        Assert.Null(CodexDesktopLifecycle.ResolveCodexHome(root, [root, server with { ParentId = 999 }]));
        Assert.Null(CodexDesktopLifecycle.ResolveCodexHome(root, [root, server with { Executable = "/tmp/codex" }]));
        Assert.Null(CodexDesktopLifecycle.ResolveCodexHome(root, [root, server with { StartTime = 99 }]));
        Assert.Null(CodexDesktopLifecycle.ResolveCodexHome(root, [root, server with { EnvironmentReadable = false }]));
        Assert.Null(CodexDesktopLifecycle.ResolveCodexHome(root, [root, server with { IsAppServer = false }]));
    }

    [Fact]
    public void DesktopScopeRejectsConflictingHomes()
    {
        var root = Desktop();
        var server = PackagedServer(Paths.Home);
        var other = PackagedServer(Path.Combine(_root, "other"), 103, 100, 103);
        Assert.Throws<CodexSyncBarException>(() => CodexDesktopLifecycle.ResolveCodexHome(root, [root, server, other]));
        root = root with { Environment = new Dictionary<string, string> { ["CODEX_HOME"] = "/other/codex" } };
        Assert.Throws<CodexSyncBarException>(() => CodexDesktopLifecycle.ResolveCodexHome(root, [root, server]));
    }

    [Fact]
    public void RestartEnvironmentCopiesSessionSettingsWithoutAppServerCredentialsOrMode()
    {
        var root = Desktop();
        var server = PackagedServer(Paths.Home) with
        {
            Environment = new Dictionary<string, string>
            {
                ["HOME"] = Paths.Home, ["WAYLAND_DISPLAY"] = "wayland-verified", ["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/fixture/bus",
                ["ELECTRON_RUN_AS_NODE"] = "1", ["OPENAI_API_KEY"] = "fixture-secret", ["LD_PRELOAD"] = "/untrusted/library.so",
                ["LANG"] = "ko_KR.UTF-8", ["LC_TIME"] = "ko_KR.UTF-8",
            },
        };
        var session = new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["WAYLAND_DISPLAY"] = "wayland-fallback", ["ANOTHER_SECRET"] = "secret" };
        var environment = CodexDesktopLifecycle.BuildRestartEnvironment(root, [root, server], Paths, session);
        Assert.Equal("wayland-verified", environment["WAYLAND_DISPLAY"]);
        Assert.Equal("/usr/bin", environment["PATH"]);
        Assert.Equal(Paths.CodexHome, environment["CODEX_HOME"]);
        Assert.Equal(Paths.Home, environment["HOME"]);
        Assert.Equal("ko_KR.UTF-8", environment["LC_TIME"]);
        Assert.DoesNotContain("ELECTRON_RUN_AS_NODE", environment.Keys);
        Assert.DoesNotContain("OPENAI_API_KEY", environment.Keys);
        Assert.DoesNotContain("ANOTHER_SECRET", environment.Keys);
        Assert.DoesNotContain("LD_PRELOAD", environment.Keys);
    }

    [Fact]
    public async Task RemotePreviewStatusDoesNotCreateDirectoriesRecoverJournalsOrLaunchCli()
    {
        var paths = Paths;
        var script = Path.GetFullPath("../../Support/gpt-switch", Path.GetDirectoryName(SourceFile())!);
        async Task<ProcessResult> ReadStatus() => await ProcessRunner.RunAsync("/bin/bash", [script, "__node", "status-readonly"],
            environment: new Dictionary<string, string>
            {
                ["HOME"] = paths.Home, ["CODEX_HOME"] = paths.CodexHome, ["GPT_SWITCH_STATE_ROOT"] = paths.StateRoot,
                ["PATH"] = Path.Combine(_root, "bin") + ":/usr/bin:/bin",
            });
        var absent = await ReadStatus();
        Assert.Equal(0, absent.ExitCode);
        Assert.Contains("active=unknown", absent.StandardOutput);
        Assert.False(Directory.Exists(paths.StateRoot));
        Assert.False(Directory.Exists(paths.CodexHome));

        var auth = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new CodexAuthFile
        {
            AuthMode = "chatgpt", Tokens = new() { AccountId = "fixture-status", AccessToken = "access", RefreshToken = "", IdToken = "id" },
        });
        Directory.CreateDirectory(paths.CodexHome, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        WindowsPathSafety.WritePrivateBytes(paths.ActiveAuthFile, auth);
        var withoutSlots = await ReadStatus();
        Assert.Equal(0, withoutSlots.ExitCode);
        Assert.Contains("active=unknown", withoutSlots.StandardOutput);
        Assert.Contains("fingerprint=" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("fixture-status")))[..12], withoutSlots.StandardOutput);
        Assert.False(Directory.Exists(paths.StateRoot));
        Assert.Equal(auth, File.ReadAllBytes(paths.ActiveAuthFile));
        paths.EnsureDirectories();
        WindowsPathSafety.WritePrivateBytes(paths.ProfileAuthFile(1), auth);
        var journal = Path.Combine(paths.StateRoot, ".swap-profiles.fixture");
        Directory.CreateDirectory(journal, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(journal, "manifest"), "state=committed\npid=99999999\n");
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var cli = Path.Combine(bin, "codex");
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
        Assert.Equal(auth, File.ReadAllBytes(paths.ActiveAuthFile));
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
            WindowsPathSafety.WritePrivateBytes(file, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new CodexAuthFile
            {
                AuthMode = "chatgpt", Tokens = new() { AccountId = "fixture-" + id, AccessToken = "access", RefreshToken = "fixture-refresh", IdToken = "id" },
            }));
            auth.ImportAuth(file, id);
            Assert.DoesNotContain("fixture-refresh", File.ReadAllText(paths.ProfileAuthFile(id)));
        }
        auth.SwitchActive(1);
        var desktop = new FailingDesktop(auth);
        var service = new LocalSwitchService(auth, paths, () => desktop);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchAsync(2));
        Assert.Equal("fixture-1", auth.ReadActiveAccountId());
        Assert.Equal(new[] { "stop:fixture-1", "start:fixture-2", "stop:fixture-2", "start:fixture-1" }, desktop.Events);
        Assert.Equal("fixture-2", auth.ReadCredentials(2).AccountId);
    }
    private sealed class FailingDesktop(AuthStore auth) : ICodexDesktopSession
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

    private static Process StartFixtureServer(string home, string listen)
    {
        var start = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("exec -a codex /usr/bin/python3 -c 'import time; time.sleep(60)' app-server --listen \"$1\"");
        start.ArgumentList.Add("syncbar-fixture");
        start.ArgumentList.Add(listen);
        start.Environment["CODEX_HOME"] = home;
        start.Environment["HOME"] = Path.GetDirectoryName(home)!;
        return Process.Start(start)!;
    }
    private static void StopFixture(Process process)
    {
        if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
