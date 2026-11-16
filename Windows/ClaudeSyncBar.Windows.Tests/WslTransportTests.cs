using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeSyncBar.Ubuntu.Backend;
using ClaudeSyncBar.Windows.Core;
using Xunit.Abstractions;
using Contracts = ClaudeSyncBar.Windows.Core.Tests.TestAuth;

namespace ClaudeSyncBar.Windows.Tests;

/// <summary>The tests that stand in for wsl.exe share one stand-in, so they run one at a time.</summary>
[CollectionDefinition("wsl", DisableParallelization = true)]
public sealed class WslCollection;

/// <summary>
/// A WSL distribution as a device: what wsl.exe is asked, how such a device is kept in the
/// configuration, and that a look made in the background leaves a stopped distribution
/// stopped. wsl.exe itself is stood in for; no distribution is started.
/// </summary>
[Collection("wsl")]
public sealed class WslTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-wsl-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsPaths _paths;
    private readonly List<IReadOnlyList<string>> _calls = [];
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase) { "Ubuntu-26.04" };
    private string _commandOutput = "claude-syncbar\n";

    public WslTransportTests()
    {
        _paths = new WindowsPaths(Path.Combine(_root, "home"), Path.Combine(_root, "data"));
        _paths.EnsureDirectories();
        WslTransport.Runner = (arguments, _, _, _) =>
        {
            lock (_calls) _calls.Add(arguments);
            if (arguments.Contains("--running")) return Result(string.Join("\r\n", _running));
            if (arguments.Contains("--list")) return Result("Ubuntu-26.04\r\ndocker-desktop\r\nDebian\r\n");
            if (arguments.Contains("id")) return Result("tester\n");
            return Result(_commandOutput);
        };
    }

    public void Dispose()
    {
        WslTransport.Runner = null;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static Task<ProcessResult> Result(string output) => Task.FromResult(new ProcessResult(0, output, ""));

    private static SshDeviceConfiguration Device(bool enabled = true) => new()
    {
        Id = "device-wsl", DisplayName = "Ubuntu-26.04", Host = "Ubuntu-26.04", Username = "tester",
        Authentication = WslTransport.Authentication, Enabled = enabled,
    };

    private IReadOnlyList<IReadOnlyList<string>> Commands()
    {
        lock (_calls) return _calls.Where(call => call.Contains("--exec")).ToArray();
    }

    [Fact]
    public void ACommandRunsInTheDistributionAsItsUserFromItsHome()
    {
        var arguments = WslTransport.Arguments(Device(), "~/.local/bin/claude-switch __node version");

        Assert.Equal(["--distribution", "Ubuntu-26.04", "--user", "tester", "--cd", "~", "--exec", "sh", "-c"], arguments.Take(9));
        // The command is one argument of its own, never spliced into the script that runs it.
        Assert.Equal("~/.local/bin/claude-switch __node version", arguments[^1]);
        Assert.Contains("exec sh -c \"$1\"", arguments[9]);
        // Windows' PATH entries are dropped, so a Windows claude or node never answers for the distribution's.
        Assert.Contains("/mnt/?/*", arguments[9]);
    }

    [Theory]
    [InlineData("docker-desktop")]
    [InlineData("docker-desktop-data")]
    [InlineData("-d")]
    [InlineData("Ubuntu 26")]
    [InlineData("")]
    public void ANameThatIsNoDistributionIsRefused(string name)
    {
        Assert.Throws<ClaudeSyncBarException>(() => WslTransport.ValidateDistribution(name));
    }

    [Fact]
    public async Task TheListLeavesOutDockerAndSaysWhichRun()
    {
        var found = await WslTransport.DiscoverAsync();

        Assert.Equal(["Ubuntu-26.04", "Debian"], found.Select(d => d.Name));
        Assert.True(found[0].IsRunning);
        Assert.False(found[1].IsRunning);
        // wsl.exe writes UTF-16 unless told otherwise; names read that way still come out whole.
        Assert.Equal(["Ubuntu"], WslTransport.ParseList("U\0b\0u\0n\0t\0u\0\r\0\n\0"));
    }

    [Fact]
    public void TheConfigurationKeepsAWslDeviceAndNoSshSecretWithIt()
    {
        var store = new ConfigurationStore(_paths);
        var configuration = new AppConfiguration { Accounts = [new() { Id = 1, Email = "one@example.invalid" }], NextAccountId = 2 };
        store.Save(configuration);

        store.UpsertDevice(configuration, Device(enabled: false));
        var kept = Assert.Single(store.LoadOrCreate().Devices);
        Assert.Equal("wsl", kept.Authentication);
        Assert.Equal("Ubuntu-26.04", kept.Host);

        var withKey = Device(enabled: false);
        withKey.Id = "device-other";
        withKey.IdentityFile = @"C:\keys\id_ed25519";
        Assert.Throws<ClaudeSyncBarException>(() => store.UpsertDevice(configuration, withKey));
        var docker = Device(enabled: false);
        docker.Id = "device-docker";
        docker.Host = "docker-desktop";
        Assert.Throws<ClaudeSyncBarException>(() => store.UpsertDevice(configuration, docker));
    }

    [Fact]
    public async Task TheDeviceIsReachedWithWslExeNotSsh()
    {
        var service = new SshDeviceService(new AuthStore(_paths), _paths);

        var test = await service.TestConnectionAsync(Device());

        Assert.True(test.IsReachable, test.Message);
        var command = Assert.Single(Commands());
        Assert.Equal("echo claude-syncbar", command[^1]);
        Assert.Equal("Ubuntu-26.04", command[1]);
    }

    [Fact]
    public async Task ALookInTheBackgroundLeavesAStoppedDistributionStopped()
    {
        _running.Clear();
        var service = new SshDeviceService(new AuthStore(_paths), _paths);
        var configuration = new AppConfiguration { Accounts = [new() { Id = 1, Email = "one@example.invalid" }], NextAccountId = 2, Devices = [Device()] };

        IReadOnlyList<DeviceStatus> statuses;
        using (WslTransport.LeaveStopped()) statuses = await service.FetchStatusesAsync(configuration, 1);

        var status = Assert.Single(statuses, s => s.Id == "device-wsl");
        Assert.False(status.IsReachable);
        Assert.Equal("stopped", status.CliState);
        Assert.Empty(Commands());

        // Outside such a look, as when the user applies an account, the command runs and so starts it.
        _commandOutput = "active=1 fingerprint=unknown cli=ready\n";
        statuses = await service.FetchStatusesAsync(configuration, 1);
        Assert.True(Assert.Single(statuses, s => s.Id == "device-wsl").IsReachable);
        Assert.Single(Commands());
    }

    [Fact]
    public async Task AddingADistributionMakesADeviceThatWaitsToBeInstalled()
    {
        var auth = new AuthStore(_paths);
        var source = Path.Combine(_paths.LoginSessionsDirectory, "source.json");
        WindowsPathSafety.WritePrivateBytes(source, Contracts.Bytes(Contracts.Create("wsl-account-1", "synthetic-access", "synthetic-refresh",
            email: "one@example.invalid", expiresAt: DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds())));
        auth.ImportAuth(source, 1);
        auth.SwitchActive(1);
        new ConfigurationStore(_paths).Save(new AppConfiguration { Accounts = [new() { Id = 1, Email = "one@example.invalid" }], NextAccountId = 2 });
        using var service = new ManagementService(_paths, new UsageService(new HttpClient(new Offline())), _ => { }, watch: false,
            new ResumeHooks(_ => [], _ => false, _ => throw new InvalidOperationException("No test may continue a real session.")));
        try
        {
            var listed = Assert.IsType<JsonArray>(JsonSerializer.SerializeToNode(await Call(service, "wsl.list", new { }), Wire.Json));
            Assert.Equal(["Ubuntu-26.04", "Debian"], listed.Select(d => d!["name"]!.GetValue<string>()));
            Assert.All(listed, d => Assert.False(d!["registered"]!.GetValue<bool>()));

            var saved = Assert.IsType<JsonObject>(JsonSerializer.SerializeToNode(await Call(service, "wsl.add", new { distribution = "Ubuntu-26.04" }), Wire.Json));
            Assert.Equal("wsl", saved["authentication"]!.GetValue<string>());
            Assert.Equal("Ubuntu-26.04", saved["host"]!.GetValue<string>());
            Assert.Equal("tester", saved["username"]!.GetValue<string>());
            // Registered, not yet part of switching: installing it comes next.
            Assert.False(saved["enabled"]!.GetValue<bool>());

            listed = Assert.IsType<JsonArray>(JsonSerializer.SerializeToNode(await Call(service, "wsl.list", new { }), Wire.Json));
            Assert.True(listed[0]!["registered"]!.GetValue<bool>());
            var again = await Assert.ThrowsAsync<RpcException>(() => Call(service, "wsl.add", new { distribution = "ubuntu-26.04" }));
            Assert.Equal("device_exists", again.Code);
            var unknown = await Assert.ThrowsAsync<RpcException>(() => Call(service, "wsl.add", new { distribution = "Fedora" }));
            Assert.Equal("device_unavailable", unknown.Code);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    private static Task<object?> Call(ManagementService service, string method, object parameters) =>
        service.DispatchAsync(method, JsonSerializer.SerializeToElement(parameters, Wire.Json), CancellationToken.None);

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }
}

/// <summary>Runs only when SYNCBAR_LIVE_WSL_DISTRO names a WSL distribution of this PC to try it in.</summary>
public sealed class LiveWslFactAttribute : FactAttribute
{
    public LiveWslFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SYNCBAR_LIVE_WSL_DISTRO")))
            Skip = "시험할 WSL 배포판(SYNCBAR_LIVE_WSL_DISTRO)이 지정되지 않았습니다.";
    }
}

/// <summary>
/// The whole WSL device path through the real wsl.exe in a real distribution: add it, install
/// and activate, switch that device alone and then every device, read its CLI and token usage,
/// and remove it. The accounts are made-up logins in an isolated Windows home, and inside the
/// distribution every command gets a throwaway HOME under /tmp, so the user's own ~/.claude and
/// login there are never read or written.
/// </summary>
[Collection("wsl")]
public sealed class LiveWslTests(ITestOutputHelper output)
{
    private static readonly string WslExe = Path.Combine(Environment.SystemDirectory, "wsl.exe");
    private static readonly Dictionary<string, string?> Utf8 = new() { ["WSL_UTF8"] = "1" };

    [LiveWslFact]
    public async Task AddInstallAndSwitchThroughWslExe()
    {
        var distribution = Environment.GetEnvironmentVariable("SYNCBAR_LIVE_WSL_DISTRO")!;
        var home = "/tmp/syncbar-live-wsl-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "syncbar-live-wsl-" + Guid.NewGuid().ToString("N"));
        var paths = new WindowsPaths(Path.Combine(root, "home"), Path.Combine(root, "data"));
        ManagementService? service = null;
        // The app's own arguments, with HOME and the starting directory moved to the throwaway home.
        WslTransport.Runner = (arguments, input, token, timeout) =>
        {
            var isolated = arguments.ToList();
            var exec = isolated.IndexOf("--exec");
            if (exec >= 0)
            {
                isolated.InsertRange(exec + 1, ["env", "HOME=" + home]);
                var cd = isolated.IndexOf("--cd");
                if (cd >= 0 && cd < exec) isolated[cd + 1] = home;
            }

            return ProcessRunner.RunAsync(WslExe, isolated, input, token, timeout, Utf8);
        };
        try
        {
            // A stand-in CLI, so that reading the device's CLI finds one without the user's own.
            await InDistribution(distribution, "set -eu; umask 077; mkdir -p \"$1/.local/bin\"; "
                + "printf '#!/bin/sh\\necho \"9.9.9 (Claude Code)\"\\n' > \"$1/.local/bin/claude\"; chmod 755 \"$1/.local/bin/claude\"", home);
            paths.EnsureDirectories();
            var auth = new AuthStore(paths);
            var accounts = new List<AccountProfile>();
            for (var profile = 1; profile <= 2; profile++)
            {
                var source = Path.Combine(paths.LoginSessionsDirectory, $"source-{profile}.json");
                WindowsPathSafety.WritePrivateBytes(source, Contracts.Bytes(Contracts.Create(
                    "live-account-" + profile, "synthetic-access-" + profile, "synthetic-refresh-" + profile, email: $"live{profile}@example.invalid",
                    expiresAt: DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds())));
                auth.ImportAuth(source, profile);
                accounts.Add(new() { Id = profile, Email = $"live{profile}@example.invalid", CustomAlias = "시험" + profile });
            }

            auth.SwitchActive(1);
            new ConfigurationStore(paths).Save(new AppConfiguration { Accounts = accounts, NextAccountId = 3 });
            service = new ManagementService(paths, new UsageService(new HttpClient(new UsageHandler())), _ => { }, watch: false,
                new ResumeHooks(_ => [], _ => false, _ => throw new InvalidOperationException("No test may continue a real session.")));

            // 1. The distribution is found and becomes a device that waits to be installed.
            var listed = Assert.IsType<JsonArray>(JsonSerializer.SerializeToNode(await Dispatch(service, "wsl.list", null), Wire.Json));
            Log("1. 배포판 목록", listed.ToJsonString());
            Assert.Contains(listed, d => string.Equals(d!["name"]!.GetValue<string>(), distribution, StringComparison.OrdinalIgnoreCase));
            var saved = await Node(service, "wsl.add", new { distribution });
            var id = saved["id"]!.GetValue<string>();
            Log("1. 등록", $"id={id} user={saved["username"]} enabled={saved["enabled"]}");
            Assert.Equal("wsl", saved["authentication"]!.GetValue<string>());
            Assert.False(saved["enabled"]!.GetValue<bool>());

            // 2. A command runs in it.
            var test = await Node(service, "device.test", new { deviceId = id });
            Log("2. 연결 테스트", $"reachable={test["isReachable"]} message={test["message"]}");
            Assert.True(test["isReachable"]!.GetValue<bool>(), test["message"]?.ToString());

            // 3. Installing writes the helper through wsl.exe, gives the device the logins and activates it.
            var job = await Wait(service, await Call<OperationView>(service, "device.bootstrap", new { deviceId = id }));
            Log("3. 설치 및 활성화", $"{job.State} · {job.Message}");
            Assert.True(job.State == "completed", job.Message);
            var device = await Device(service, id);
            Log("3. 장치 상태", $"kind={device.Kind} reachable={device.IsReachable} current={device.CurrentProfileId} status={device.Status} detail={device.Detail}");
            Assert.Equal("wsl", device.Kind);
            Assert.True(device.Enabled && device.IsReachable && device.Status == "ready");
            Assert.Equal(1, device.CurrentProfileId);
            Assert.Equal("live-account-1", await AccountIn(distribution, home));
            // What the distribution holds can be used but not renewed: Windows keeps the refresh token.
            var login = await InDistribution(distribution, "stat -c %a \"$1/.claude/.credentials.json\"; cat \"$1/.claude/.credentials.json\"", home);
            Assert.StartsWith("600", login.TrimStart());
            Assert.Contains("synthetic-access-1", login);
            Assert.DoesNotContain("synthetic-refresh", login);

            // 4. This device alone: the PC keeps its account.
            var preview = await Call<SwitchPreview>(service, "preview", new { profileId = 2, deviceId = id });
            Log("4. 이 장치에만 미리보기", $"canApply={preview.CanApply} included={string.Join(",", preview.Targets.Where(t => t.Included).Select(t => t.Id))} {preview.BlockingReason}");
            Assert.True(preview.CanApply, preview.BlockingReason);
            Assert.Equal("ssh:" + id, Assert.Single(preview.Targets, t => t.Included).Id);
            job = await Apply(service, preview);
            Log("4. 이 장치에만 적용", $"{job.State} · {job.Message} · {string.Join(", ", job.Targets.Select(t => $"{t.Id}={t.State}"))}");
            Assert.True(job.State == "completed", job.Message);
            Assert.Equal("live-account-2", await AccountIn(distribution, home));
            Assert.Equal("live-account-1", auth.ReadActiveAccountId());

            // 5. Every device, then back again: the distribution follows this PC.
            foreach (var (profile, account) in new[] { (2, "live-account-2"), (1, "live-account-1") })
            {
                preview = await Call<SwitchPreview>(service, "preview", new { profileId = profile });
                Log($"5. 모든 장치 → 계정 {profile}", $"canApply={preview.CanApply} " + string.Join(", ", preview.Targets.Where(t => t.Included).Select(t => $"{t.Id}:{t.Action}")));
                Assert.True(preview.CanApply, preview.BlockingReason);
                job = await Apply(service, preview);
                Log($"5. 모든 장치 → 계정 {profile}", $"{job.State} · {job.Message}");
                Assert.True(job.State == "completed", job.Message);
                Assert.Equal(account, await AccountIn(distribution, home));
                Assert.Equal(account, auth.ReadActiveAccountId());
                Assert.Equal(profile, (await Device(service, id)).CurrentProfileId);
            }

            // 6. The device's CLI and token usage are read through the helper.
            var cli = await Node(service, "cli.status", new { deviceId = "ssh:" + id });
            Log("6. 배포판 CLI", cli.ToJsonString());
            Assert.Contains("9.9.9", cli.ToJsonString());
            var tokens = await Node(service, "tokens", null);
            var remoteTokens = (tokens["devices"] as JsonArray)!.OfType<JsonObject>().Single(d => d["id"]!.GetValue<string>() == id);
            Log("6. 배포판 토큰 집계", remoteTokens.ToJsonString());
            Assert.True(remoteTokens["isReachable"]!.GetValue<bool>());
            Assert.Null(remoteTokens["error"]);

            // 7. Removing the device leaves the distribution's account as it is.
            await Node(service, "device.delete", new { deviceId = id });
            var snapshot = await Call<ManagementSnapshot>(service, "snapshot");
            Assert.DoesNotContain(snapshot.Devices, d => d.Kind != "local");
            Assert.Equal("live-account-1", await AccountIn(distribution, home));
            Log("7. 장치 삭제", "목록에서 빠졌고 배포판의 계정은 그대로");
        }
        finally
        {
            if (service is not null)
            {
                await service.StopAsync();
                service.Dispose();
            }

            WslTransport.Runner = null;
            try { await InDistribution(distribution, "case \"$1\" in /tmp/syncbar-live-wsl-*) rm -rf \"$1\" ;; esac", home); }
            catch (Exception error) when (error is ClaudeSyncBarException or InvalidOperationException) { }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void Log(string step, string detail) => output.WriteLine($"{step}: {detail}");

    // Run directly, without the app's transport: sets up, reads and clears the throwaway home.
    private static async Task<string> InDistribution(string distribution, string script, string argument)
    {
        var result = await ProcessRunner.RunAsync(WslExe, ["--distribution", distribution, "--exec", "sh", "-c", script, "syncbar-test", argument],
            timeout: TimeSpan.FromSeconds(60), environment: Utf8);
        if (result.ExitCode != 0) throw new InvalidOperationException("WSL 시험 준비 명령이 실패했습니다: " + result.CombinedOutput.Trim());
        return result.StandardOutput;
    }

    private static async Task<string> AccountIn(string distribution, string home)
    {
        using var document = JsonDocument.Parse(await InDistribution(distribution, "cat \"$1/.claude.json\"", home));
        return document.RootElement.GetProperty("oauthAccount").GetProperty("accountUuid").GetString()!;
    }

    private static async Task<object?> Dispatch(ManagementService service, string method, object? parameters) =>
        await service.DispatchAsync(method, JsonSerializer.SerializeToElement(parameters ?? new { }, Wire.Json), CancellationToken.None);

    private static async Task<T> Call<T>(ManagementService service, string method, object? parameters = null) =>
        Assert.IsAssignableFrom<T>(await Dispatch(service, method, parameters));

    private static async Task<JsonObject> Node(ManagementService service, string method, object? parameters) =>
        Assert.IsType<JsonObject>(JsonSerializer.SerializeToNode(await Dispatch(service, method, parameters), Wire.Json));

    private static async Task<DeviceView> Device(ManagementService service, string id) =>
        Assert.Single((await Call<ManagementSnapshot>(service, "snapshot")).Devices, d => d.Id == "ssh:" + id);

    private static async Task<OperationView> Apply(ManagementService service, SwitchPreview preview) => await Wait(service,
        await Call<OperationView>(service, "apply", new { previewId = preview.PreviewId, requestId = Guid.NewGuid().ToString("N") }));

    private static async Task<OperationView> Wait(ManagementService service, OperationView operation)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (!operation.IsComplete)
        {
            Assert.True(DateTime.UtcNow < deadline, "작업이 끝나지 않았습니다: " + operation.Message);
            await Task.Delay(100);
            operation = await Call<OperationView>(service, "operation", new { operationId = operation.Id });
        }

        while ((await Call<ManagementSnapshot>(service, "snapshot")).IsBusy)
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(100);
        }

        return operation;
    }

    // A usage endpoint that answers without the network; no Claude quota is in it.
    private sealed class UsageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }
}
