using System.Diagnostics;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using ClaudeSyncBar.Ubuntu.Backend;
using ClaudeSyncBar.Windows.Core;
using ClaudeSyncBar.Windows.Core.Tests;
using TestAuth = ClaudeSyncBar.Windows.Core.Tests.TestAuth;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ClaudeSyncBar.Windows.Tests;

public sealed class WindowsPlatformTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csb-win-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsPaths _paths;

    public WindowsPlatformTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new WindowsPaths(Path.Combine(_root, "home"));
        Directory.CreateDirectory(_paths.Home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void IsolatedPathsStayInsideTheGivenHome()
    {
        Assert.True(_paths.IsIsolated);
        Assert.Equal(Path.Combine(_paths.Home, "AppData", "Local", "claude-syncbar"), _paths.StateRoot);
        Assert.Equal(Path.Combine(_paths.Home, "AppData", "Roaming", "Claude"), _paths.DesktopUserDataDirectory);
        Assert.Equal(Path.Combine(_paths.Home, ".claude", ".credentials.json"), _paths.ActiveAuthFile);
        Assert.Equal(Path.Combine(_paths.Home, ".claude.json"), _paths.ClaudeConfigFile);
        Assert.StartsWith(_paths.StateRoot, _paths.DesktopLoginsDirectory);
    }

    [Fact]
    public void SecretsRoundTripAndAreBoundToTheirName()
    {
        var store = new WindowsSecretStore(_paths);
        store.Save("s3cret value", "device-1.password");
        Assert.Equal("s3cret value", store.Read("device-1.password"));
        var directory = Path.Combine(_paths.StateRoot, "secrets");
        Assert.DoesNotContain("s3cret", File.ReadAllText(Path.Combine(directory, "device-1.password.bin")));
        File.Copy(Path.Combine(directory, "device-1.password.bin"), Path.Combine(directory, "device-2.password.bin"));
        Assert.Throws<ClaudeSyncBarException>(() => store.Read("device-2.password"));
        store.Delete("device-1.password");
        Assert.Null(store.Read("device-1.password"));
    }

    [Fact]
    public void PrivateFilesRejectReadersOtherThanTheUser()
    {
        WindowsPathSafety.EnsurePrivateDirectory(_paths.StateRoot, "상태");
        var file = Path.Combine(_paths.StateRoot, "private.bin");
        WindowsPathSafety.WritePrivateBytes(file, [1, 2, 3]);
        Assert.Equal([1, 2, 3], WindowsPathSafety.ReadPrivateFile(file, "비공개 파일", 16));
        var security = new FileInfo(file).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
        Assert.Throws<ClaudeSyncBarException>(() => WindowsPathSafety.EnsurePrivateFile(file, "비공개 파일", 16));
    }

    [Fact]
    public async Task StartsWithALoginOthersMayReadAndNamesOneOthersMayChange()
    {
        // The user's case: Claude Code rewrote its login with the .claude folder's ACL,
        // where the Codex sandbox had given its users read access.
        _paths.EnsureDirectories();
        var auth = new AuthStore(_paths);
        var source = Path.Combine(_paths.LoginSessionsDirectory, "source.json");
        WindowsPathSafety.WritePrivateBytes(source, TestAuth.Bytes(TestAuth.Create("account-1", "synthetic-access", "synthetic-refresh",
            email: "one@example.invalid", expiresAt: DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds())));
        auth.ImportAuth(source, 1);
        auth.SwitchActive(1);
        new ConfigurationStore(_paths).Save(new AppConfiguration { Accounts = [new() { Id = 1, Email = "one@example.invalid" }], NextAccountId = 2 });

        Allow(_paths.ActiveAuthFile, FileSystemRights.ReadAndExecute);
        var snapshot = await StartAndSnapshotAsync();
        Assert.Equal(1, snapshot.ActiveProfileId);
        Assert.DoesNotContain("시작 복구", snapshot.Error ?? "");

        // Write access for others is still refused, and the start says why.
        Allow(_paths.ActiveAuthFile, FileSystemRights.Modify);
        snapshot = await StartAndSnapshotAsync();
        Assert.StartsWith("시작 복구를 완료하지 못했습니다.", snapshot.Error);
        Assert.Contains("인증 파일을(를) 다른 사용자가 변경할 수 있어 안전하지 않습니다.", snapshot.Error);
    }

    private async Task<ManagementSnapshot> StartAndSnapshotAsync()
    {
        var service = new ManagementService(_paths, new UsageService(new HttpClient(new OfflineHandler())), _ => { }, watch: false,
            new ResumeHooks(_ => [], _ => false, _ => throw new InvalidOperationException("No test may continue a real session.")));
        try { return (ManagementSnapshot)(await service.DispatchAsync("snapshot", default, CancellationToken.None))!; }
        finally
        {
            await service.StopAsync();
            service.Dispose();
        }
    }

    private static void Allow(string path, FileSystemRights rights)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), rights, AccessControlType.Allow));
        file.SetAccessControl(security);
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    [Fact]
    public async Task ConfigLockWaitsForTheHolderAndTakesOverAStaleOne()
    {
        var config = Path.Combine(_paths.Home, ".claude.json");
        var first = ClaudeConfigLock.Acquire(config);
        var second = Task.Run(() => ClaudeConfigLock.Acquire(config));
        await Task.Delay(300);
        Assert.False(second.IsCompleted);
        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();

        Directory.CreateDirectory(config + ".lock");
        Directory.SetLastWriteTimeUtc(config + ".lock", DateTime.UtcNow.AddMinutes(-1));
        var started = DateTime.UtcNow;
        using (ClaudeConfigLock.Acquire(config)) Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.False(Directory.Exists(config + ".lock"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_2.42304.2.0_x64__pzs8sxrjxfjjc\app\Claude.exe", true)]
    [InlineData(@"C:\Users\me\AppData\Local\AnthropicClaude\app-0.9.3\claude.exe", true)]
    [InlineData(@"C:\Users\me\AppData\Roaming\Claude\claude-code\2.1.299\f0584be0fbaf\claude.exe", false)]
    [InlineData(@"C:\Users\me\.local\bin\claude.exe", false)]
    public void TellsClaudeDesktopFromTheCli(string path, bool desktop)
    {
        Assert.Equal(desktop, ClaudeDesktopLifecycle.IsDesktopExecutable(path));
        Assert.Equal(!desktop, WindowsProcess.IsClaudeCliCommand(path, [path]));
    }

    [Fact]
    public void RecognisesTheNpmCliUnderNode()
    {
        var node = @"C:\Program Files\nodejs\node.exe";
        Assert.True(WindowsProcess.IsClaudeCliCommand(node, [node, @"C:\Users\me\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js"]));
        Assert.False(WindowsProcess.IsClaudeCliCommand(node, [node, @"C:\work\server.js"]));
    }

    [Fact]
    public void SplitsCommandLinesTheWayWindowsProgramsDo()
    {
        Assert.Equal(["a b", "c\"d", @"e\\"], WindowsProcess.SplitCommandLine("\"a b\" c\\\"d \"e\\\\\\\\\""));
        Assert.Equal(["--user-data-dir=C:\\x y", "--type=renderer"], WindowsProcess.SplitCommandLine("\"--user-data-dir=C:\\x y\" --type=renderer"));
    }

    [Fact]
    public void ReadsAnotherProcessCommandLineDirectoryAndEnvironment()
    {
        var directory = Path.Combine(_root, "work dir");
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c ping -n 6 127.0.0.1 >nul")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
        };
        start.Environment["CSB_PROBE"] = "yes";
        using var child = Process.Start(start)!;
        try
        {
            Thread.Sleep(300);
            using var captured = WindowsProcess.Capture(child.Id);
            Assert.NotNull(captured);
            Assert.Contains("ping", captured.CommandLine);
            Assert.Equal(Environment.ProcessId, captured.ParentId);
            Assert.Equal(directory, captured.CurrentDirectory, ignoreCase: true);
            Assert.True(captured.EnvironmentReadable);
            Assert.Equal("yes", captured.Environment["CSB_PROBE"]);
            Assert.True(WindowsProcess.DescendsFrom(child.Id, Environment.ProcessId));
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void LoginOpenerWritesTheQuotedAddressExactly()
    {
        var directory = Path.Combine(_root, "login");
        Directory.CreateDirectory(directory);
        var opener = Path.Combine(directory, ClaudeLoginService.OpenerName);
        File.WriteAllText(opener, ClaudeLoginService.OpenerScript);
        var target = Path.Combine(directory, "login-url");
        const string url = "https://claude.com/cai/oauth/authorize?code=true&client_id=abc&scope=user%3Aprofile+user%3Ainference&state=x!y";
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /s /c \"\"{opener}\" \"{url}\"\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
        };
        start.Environment["CLAUDE_SYNCBAR_LOGIN_URL_FILE"] = target;
        using (var process = Process.Start(start)!) Assert.True(process.WaitForExit(10000));
        Assert.Equal(url, File.ReadAllText(target).Trim());
        Assert.False(File.Exists(target + ".tmp"));
    }

    [Fact]
    public void PicksTheNewestCliClaudeDesktopKeeps()
    {
        var root = Path.Combine(_root, "claude-code");
        foreach (var (version, build) in new[] { ("2.1.9", "aaa"), ("2.1.10", "bbb") })
        {
            Directory.CreateDirectory(Path.Combine(root, version, build));
            File.WriteAllText(Path.Combine(root, version, build, "claude.exe"), "");
        }

        Assert.Equal(Path.Combine(root, "2.1.10", "bbb", "claude.exe"), ClaudeCliLocator.FindDesktopBundled(root));
    }

    [Fact]
    public void LaunchAtLoginOfAnIsolatedHomeNeverTouchesTheRegistry()
    {
        Assert.False(BackendPlatform.LaunchAtLogin(_paths));
        BackendPlatform.SetLaunchAtLogin(_paths, true);
        Assert.True(BackendPlatform.LaunchAtLogin(_paths));
        BackendPlatform.SetLaunchAtLogin(_paths, false);
        Assert.False(BackendPlatform.LaunchAtLogin(_paths));
    }

    [Fact]
    public void ProcessWhoseParentIdWasReusedIsNotTakenForADescendant()
    {
        // This app (300) was started by 200, which then ended; Windows gave 200 to a
        // newer process of Claude Desktop (100), started after this app.
        var parents = new Dictionary<int, int> { [300] = 200, [200] = 100, [100] = 10 };
        var started = new Dictionary<int, long> { [300] = 50, [200] = 90, [100] = 80, [10] = 1 };
        long? Started(int id) => started.TryGetValue(id, out var time) ? time : null;
        Assert.False(WindowsProcess.DescendsFrom(300, 100, parents, Started));

        // Started from inside the app for real: every parent is older than its child.
        started[200] = 40;
        started[100] = 30;
        Assert.True(WindowsProcess.DescendsFrom(300, 100, parents, Started));

        // Start times that cannot be read are trusted, so the app is never stopped from inside it.
        Assert.True(WindowsProcess.DescendsFrom(300, 100, parents, _ => null));
        Assert.False(WindowsProcess.DescendsFrom(300, 999, parents, _ => null));
    }

    [Fact]
    public void DesktopDataIsFollowedWhereTheStorePackageMovedIt()
    {
        var roaming = Path.Combine(_paths.Home, "AppData", "Roaming", "Claude");
        var moved = Path.Combine(_paths.LocalAppData, "Claude-Data", "claude_pkg0123456789");
        // Before the move: the classic folder with its config.
        Directory.CreateDirectory(roaming);
        File.WriteAllText(Path.Combine(roaming, "config.json"), "{}");
        Assert.Equal(roaming, _paths.DesktopUserDataDirectory);

        // The app moves its data and leaves a note where it was; the CLI it keeps moves with it.
        Directory.CreateDirectory(moved);
        File.WriteAllText(Path.Combine(moved, "config.json"), "{}");
        File.Delete(Path.Combine(roaming, "config.json"));
        File.WriteAllText(Path.Combine(roaming, ".user-data-left.claude_pkg0123456789"), "1");
        Assert.Equal(moved, _paths.DesktopUserDataDirectory);
        Assert.Equal(Path.Combine(moved, "claude-code"), _paths.DesktopBundledCliDirectory);

        // A note that names no folder there is not followed.
        File.Delete(Path.Combine(roaming, ".user-data-left.claude_pkg0123456789"));
        File.WriteAllText(Path.Combine(roaming, ".user-data-left.claude_elsewhere"), "1");
        File.WriteAllText(Path.Combine(roaming, "config.json"), "{}");
        Assert.Equal(roaming, _paths.DesktopUserDataDirectory);

        // A fresh install that only ever had the moved folder.
        Directory.Delete(roaming, recursive: true);
        Assert.Equal(moved, _paths.DesktopUserDataDirectory);
    }

    [Fact]
    public void DesktopSignInIsExchangedInTheNetworkFolderAndKeptPerAccount()
    {
        const string first = "aaaaaaaa-1111";
        const string second = "bbbbbbbb-2222";
        var profile = _paths.DesktopUserDataDirectory;
        var cookies = Path.Combine(profile, "Network", "Cookies");
        Directory.CreateDirectory(Path.GetDirectoryName(cookies)!);
        WriteConfig(first, "token-first");
        File.WriteAllText(cookies, "cookie-first");
        var sessions = Path.Combine(profile, "claude-code-sessions", first, "org-first");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "local_1.json"), "{}");
        var accounts = new ClaudeDesktopAccounts(_paths, account => account == first ? "org-first" : "org-second");

        Assert.True(accounts.RequiresSwap(second));
        Assert.NotNull(accounts.Activate(new(second, "org-second")));
        // Nothing kept for the second account yet: the app opens signed out, the sessions follow.
        Assert.False(File.Exists(cookies));
        Assert.Equal(DesktopSignIn.Needed, accounts.SignInState());
        Assert.True(File.Exists(Path.Combine(profile, "claude-code-sessions", second, "org-second", "local_1.json")));
        Assert.True(accounts.HasLogin(first));

        // The user signs in to the second account in the app.
        WriteConfig(second, "token-second");
        File.WriteAllText(cookies, "cookie-second");
        Assert.Equal(DesktopSignIn.Settled, accounts.SignInState());

        var back = accounts.Activate(new(first, "org-first"));
        Assert.NotNull(back);
        Assert.Equal("cookie-first", File.ReadAllText(cookies));
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(profile, "config.json")))!.AsObject();
        Assert.Equal(first, config["lastKnownAccountUuid"]!.GetValue<string>());
        Assert.Equal("token-first", config["oauth:tokenCache"]!.GetValue<string>());
        Assert.Equal("kept", config["locale"]!.GetValue<string>());
        Assert.True(accounts.HasLogin(second));
        Assert.True(accounts.HasTokens(second));

        accounts.Undo(back);
        Assert.Equal("cookie-second", File.ReadAllText(cookies));
    }

    [Fact]
    public void DesktopSessionsOfEveryAccountStayInOneListAcrossSwitches()
    {
        // The user's case: signed in to the second account by hand, its new session filed apart from the earlier ones.
        const string first = "aaaaaaaa-1111";
        const string second = "bbbbbbbb-2222";
        var profile = _paths.DesktopUserDataDirectory;
        string Records(string account, string organization) => Path.Combine(profile, "claude-code-sessions", account, organization);
        Directory.CreateDirectory(Path.Combine(profile, "Network"));
        Directory.CreateDirectory(Records(first, "org-first"));
        Directory.CreateDirectory(Records(second, "org-second"));
        foreach (var name in new[] { "local_1.json", "local_2.json" }) File.WriteAllText(Path.Combine(Records(first, "org-first"), name), "{}");
        File.WriteAllText(Path.Combine(Records(second, "org-second"), "local_3.json"), "{}");
        WriteConfig(second, "token-second");
        File.WriteAllText(Path.Combine(profile, "Network", "Cookies"), "cookie-second");
        var accounts = new ClaudeDesktopAccounts(_paths, account => account == first ? "org-first" : "org-second", () => [first, second]);

        var swap = accounts.Activate(new(first, "org-first"))!;
        Assert.Equal(new[] { "local_1.json", "local_2.json", "local_3.json" },
            Directory.GetFiles(Records(first, "org-first")).Select(Path.GetFileName).Order());
        Assert.False(Directory.Exists(Records(second, "org-second")));
        // Where the shared list is now is kept with forward slashes, as on Ubuntu.
        Assert.Contains("\"claude-code-sessions\":\"aaaaaaaa-1111/org-first\"",
            File.ReadAllText(Path.Combine(profile, ".claude-syncbar-login.json")));

        accounts.Undo(swap);
        Assert.Equal(new[] { "local_1.json", "local_2.json" }, Directory.GetFiles(Records(first, "org-first")).Select(Path.GetFileName).Order());
        Assert.Equal(new[] { "local_3.json" }, Directory.GetFiles(Records(second, "org-second")).Select(Path.GetFileName));
    }

    [Fact]
    public void DesktopPresetsAndFolderPermissionModesFollowTheSwitch()
    {
        const string first = "aaaaaaaa-1111";
        const string second = "bbbbbbbb-2222";
        const string presets = "persisted.ccd-session-presets.";
        var profile = _paths.DesktopUserDataDirectory;
        var storage = Path.Combine(profile, "Local Storage", "leveldb");
        Directory.CreateDirectory(Path.Combine(profile, "Network"));
        Directory.CreateDirectory(Path.Combine(profile, "claude-code-sessions", first, "org-first"));
        WriteConfig(first, "token-first");
        File.WriteAllText(Path.Combine(profile, "Network", "Cookies"), "cookie-first");
        var settings = Path.Combine(profile, "claude_desktop_config.json");
        File.WriteAllText(settings, new JsonObject
        {
            ["preferences"] = new JsonObject
            {
                ["epitaxyPrefs"] = new JsonObject
                {
                    [$"epitaxy-folder-permission-mode-v2.{first}"] = new JsonObject { [@"C:\Users\user\프로젝트"] = "bypassPermissions" },
                    [$"epitaxy-perm-mode-acks-v2.{first}"] = new JsonArray(@"C:\Users\user\프로젝트:bypassPermissions"),
                },
            },
        }.ToJsonString());
        LevelDbFixture.CreateEmpty(storage);
        LevelDbFixture.Put(storage, (presets + first, "{\"presets\":[\"컴퓨터 관리\"]}"), (presets + second, "{\"presets\":[]}"));
        var accounts = new ClaudeDesktopAccounts(_paths, account => account == first ? "org-first" : "org-second", () => [first, second]);

        var swap = accounts.Activate(new(second, "org-second"))!;
        var epitaxy = JsonNode.Parse(File.ReadAllText(settings))!["preferences"]!["epitaxyPrefs"]!;
        Assert.Equal("bypassPermissions", epitaxy[$"epitaxy-folder-permission-mode-v2.{second}"]![@"C:\Users\user\프로젝트"]!.GetValue<string>());
        Assert.NotNull(epitaxy[$"epitaxy-perm-mode-acks-v2.{second}"]);
        Assert.Equal("{\"presets\":[\"컴퓨터 관리\"]}", LevelDbFixture.Read(storage, presets + second));

        accounts.Undo(swap);
        Assert.Null(JsonNode.Parse(File.ReadAllText(settings))!["preferences"]!["epitaxyPrefs"]![$"epitaxy-folder-permission-mode-v2.{second}"]);
        Assert.Equal("{\"presets\":[]}", LevelDbFixture.Read(storage, presets + second));

        // While the app has its Local Storage open nothing is written to it, and the switch still goes ahead.
        using (var app = new FileStream(Path.Combine(storage, "LOCK"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            app.Lock(0, long.MaxValue);
            var logs = Directory.GetFiles(storage, "*.log").Length;
            var held = accounts.Activate(new(second, "org-second"))!;
            Assert.Empty(held.Stored!);
            Assert.Equal(logs, Directory.GetFiles(storage, "*.log").Length);
            Assert.True(Directory.Exists(Path.Combine(profile, "claude-code-sessions", second, "org-second")));
        }

        Assert.Equal("{\"presets\":[]}", LevelDbFixture.Read(storage, presets + second));
    }

    [Fact]
    public void DesktopPresetsOfEveryRegisteredAccountComeTogetherOnce()
    {
        const string first = "aaaaaaaa-1111";
        const string second = "bbbbbbbb-2222";
        const string third = "cccccccc-3333";
        const string presets = "persisted.ccd-session-presets.";
        var profile = _paths.DesktopUserDataDirectory;
        var storage = Path.Combine(profile, "Local Storage", "leveldb");
        Directory.CreateDirectory(Path.Combine(profile, "Network"));
        Directory.CreateDirectory(Path.Combine(profile, "claude-code-sessions", first, "org-first"));
        WriteConfig(first, "token-first");
        File.WriteAllText(Path.Combine(profile, "Network", "Cookies"), "cookie-first");
        static JsonObject Preset(string id, string name, string cwd) => new()
        {
            ["id"] = id, ["name"] = name, ["createdAt"] = 1, ["lastUsedAt"] = 2, ["target"] = new JsonObject { ["kind"] = "local", ["cwd"] = cwd },
        };
        static string Stored(params JsonObject[] list) => new JsonObject
        {
            ["state"] = new JsonObject { ["presets"] = new JsonArray(list), ["fileMirrored"] = true }, ["version"] = 4,
        }.ToJsonString();
        File.WriteAllText(Path.Combine(profile, "ccd-session-presets.json"), new JsonObject
        {
            ["byAccount"] = new JsonObject { [third] = new JsonObject { ["presets"] = new JsonArray(Preset("t1", "encoders", @"C:\Users\user\encoders")) } },
        }.ToJsonString());
        LevelDbFixture.CreateEmpty(storage);
        LevelDbFixture.Put(storage,
            (presets + first, Stored(Preset("f1", "CodexSyncBar", @"C:\Users\user\CodexSyncBar"))),
            (presets + second, Stored(Preset("s1", "컴퓨터 관리", @"C:\Users\user\컴퓨터 관리"), Preset("s2", "CodexSyncBar", @"C:\Users\user\CodexSyncBar"))));
        var accounts = new ClaudeDesktopAccounts(_paths, account => account == first ? "org-first" : "org-second", () => [first, second, third]);
        string[] Names(string account) => JsonNode.Parse(LevelDbFixture.Read(storage, presets + account)!)!["state"]!["presets"]!.AsArray()
            .Select(preset => preset!["name"]!.GetValue<string>()).ToArray();

        var swap = accounts.Activate(new(second, "org-second"))!;
        Assert.True(swap.PresetsMerged);
        Assert.Equal(new[] { "CodexSyncBar", "컴퓨터 관리", "encoders" }, Names(second));
        Assert.True(File.Exists(Path.Combine(_paths.DesktopLoginsDirectory, ".presets-merged")));

        accounts.Undo(swap);
        Assert.Equal(new[] { "컴퓨터 관리", "CodexSyncBar" }, Names(second));
        Assert.False(File.Exists(Path.Combine(_paths.DesktopLoginsDirectory, ".presets-merged")));
    }

    private void WriteConfig(string owner, string token)
    {
        Directory.CreateDirectory(_paths.DesktopUserDataDirectory);
        File.WriteAllText(Path.Combine(_paths.DesktopUserDataDirectory, "config.json"),
            new JsonObject { ["locale"] = "kept", ["lastKnownAccountUuid"] = owner, ["oauth:tokenCache"] = token }.ToJsonString());
    }
}
