using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeSyncBar.Ubuntu.Backend;
using ClaudeSyncBar.Windows.Core;
using Xunit.Abstractions;
using Contracts = ClaudeSyncBar.Windows.Core.Tests.TestAuth;

namespace ClaudeSyncBar.Windows.Tests;

/// <summary>Runs only when SYNCBAR_LIVE_SSH_HOST names a throwaway SSH server set up for it.</summary>
public sealed class LiveSshFactAttribute : FactAttribute
{
    public LiveSshFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SYNCBAR_LIVE_SSH_HOST")))
            Skip = "실제 SSH 시험 서버(SYNCBAR_LIVE_SSH_*)가 지정되지 않았습니다.";
    }
}

/// <summary>
/// The whole SSH device path through Windows' own ssh.exe and scp.exe against a real
/// OpenSSH server: register, check and trust the host key, install and activate,
/// switch that device alone and then every device, a passphrase key through the askpass
/// helper, the CLI and token reads, and removal. The accounts are made-up logins in an
/// isolated home. The server must be a throwaway one whose sessions, SFTP included, run in
/// their own HOME (SYNCBAR_LIVE_SSH_REMOTE_HOME, readable from Windows). The user's
/// known_hosts receives the test server's key during the run and is put back byte for byte.
/// </summary>
public sealed class LiveSshTests(ITestOutputHelper output)
{
    private static string Need(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException(name + "가 필요합니다.");

    [LiveSshFact]
    public async Task RegisterTrustInstallAndSwitchThroughWindowsOpenSsh()
    {
        var host = Need("SYNCBAR_LIVE_SSH_HOST");
        var port = int.Parse(Need("SYNCBAR_LIVE_SSH_PORT"));
        var user = Need("SYNCBAR_LIVE_SSH_USER");
        var key = Need("SYNCBAR_LIVE_SSH_KEY");
        var passphraseKey = Need("SYNCBAR_LIVE_SSH_PASSPHRASE_KEY");
        var passphrase = File.ReadAllText(Need("SYNCBAR_LIVE_SSH_PASSPHRASE_FILE")).Trim();
        var remoteHome = Need("SYNCBAR_LIVE_SSH_REMOTE_HOME");
        var knownHosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");
        var savedKnownHosts = File.Exists(knownHosts) ? await File.ReadAllBytesAsync(knownHosts) : null;
        var root = Path.Combine(Path.GetTempPath(), "syncbar-live-ssh-" + Guid.NewGuid().ToString("N"));
        var paths = new WindowsPaths(Path.Combine(root, "home"), Path.Combine(root, "data"));
        ManagementService? service = null;
        try
        {
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

            // 1. Registering only saves the device; it takes no part in switching until it is installed.
            var saved = await Node(service, "device.save", new
            {
                device = new { displayName = "실제 SSH 시험", host, port, username = user, authentication = "privateKey", identityFile = key, enabled = false },
            });
            var id = saved["id"]!.GetValue<string>();
            Log("1. 등록", $"id={id} enabled={saved["enabled"]}");
            Assert.False(saved["enabled"]!.GetValue<bool>());

            // 2. An unknown host key is shown for comparison before anything is sent to the server.
            var test = await Node(service, "device.test", new { deviceId = id });
            Log("2. 연결 테스트(신뢰 전)", $"reachable={test["isReachable"]} message={test["message"]}");
            Assert.NotNull(test["hostKey"]);
            var inspected = await Node(service, "device.trust", new { deviceId = id });
            var hostKey = Assert.IsType<JsonObject>(inspected["hostKey"]);
            Log("2. 호스트 키", $"trusted={inspected["trusted"]} {hostKey["lookupName"]} {hostKey["fingerprint"]}");
            Assert.False(inspected["trusted"]!.GetValue<bool>());
            var trusted = await Node(service, "device.trust", new JsonObject { ["deviceId"] = id, ["approvedKey"] = hostKey.DeepClone() });
            Assert.True(trusted["trusted"]!.GetValue<bool>());
            test = await Node(service, "device.test", new { deviceId = id });
            Log("2. 연결 테스트(신뢰 후)", $"reachable={test["isReachable"]} message={test["message"]}");
            Assert.True(test["isReachable"]!.GetValue<bool>(), test["message"]?.ToString());

            // 3. Installing uploads the helper with scp, gives the device the logins and activates it.
            var job = await Wait(service, await Call<OperationView>(service, "device.bootstrap", new { deviceId = id }));
            Log("3. 설치 및 활성화", $"{job.State} · {job.Message}");
            Assert.True(job.State == "completed", job.Message);
            var device = await Device(service, id);
            Log("3. 장치 상태", $"reachable={device.IsReachable} current={device.CurrentProfileId} status={device.Status} detail={device.Detail}");
            Assert.True(device.Enabled && device.IsReachable && device.Status == "ready");
            Assert.Equal(1, device.CurrentProfileId);
            Assert.Equal("live-account-1", RemoteAccount(remoteHome));

            // 4. This device alone: the PC keeps its account.
            var preview = await Call<SwitchPreview>(service, "preview", new { profileId = 2, deviceId = id });
            Log("4. 이 장치에만 미리보기", $"canApply={preview.CanApply} included={string.Join(",", preview.Targets.Where(t => t.Included).Select(t => t.Id))} {preview.BlockingReason}");
            Assert.True(preview.CanApply, preview.BlockingReason);
            Assert.Equal("ssh:" + id, Assert.Single(preview.Targets, t => t.Included).Id);
            job = await Apply(service, preview);
            Log("4. 이 장치에만 적용", $"{job.State} · {job.Message} · {string.Join(", ", job.Targets.Select(t => $"{t.Id}={t.State}"))}");
            Assert.True(job.State == "completed", job.Message);
            Assert.Equal("live-account-2", RemoteAccount(remoteHome));
            Assert.Equal("live-account-1", auth.ReadActiveAccountId());

            // 5. Every device, then back again.
            foreach (var (profile, account) in new[] { (2, "live-account-2"), (1, "live-account-1") })
            {
                preview = await Call<SwitchPreview>(service, "preview", new { profileId = profile });
                Log($"5. 모든 장치 → 계정 {profile}", $"canApply={preview.CanApply} " + string.Join(", ", preview.Targets.Where(t => t.Included).Select(t => $"{t.Id}:{t.Action}")));
                Assert.True(preview.CanApply, preview.BlockingReason);
                job = await Apply(service, preview);
                Log($"5. 모든 장치 → 계정 {profile}", $"{job.State} · {job.Message}");
                Assert.True(job.State == "completed", job.Message);
                Assert.Equal(account, RemoteAccount(remoteHome));
                Assert.Equal(account, auth.ReadActiveAccountId());
                Assert.Equal(profile, (await Device(service, id)).CurrentProfileId);
            }

            // 6. A key with a passphrase: ssh.exe asks the bundled askpass helper for it.
            var second = await Node(service, "device.save", new
            {
                device = new { displayName = "암호 키 시험", host, port, username = user, authentication = "privateKey", identityFile = passphraseKey, enabled = false },
                passphrase,
            });
            var secondId = second["id"]!.GetValue<string>();
            Assert.True(second["hasKeyPassphrase"]!.GetValue<bool>());
            var secondTest = await Node(service, "device.test", new { deviceId = secondId });
            Log("6. 암호 키 연결 테스트", $"reachable={secondTest["isReachable"]} message={secondTest["message"]}");
            Assert.True(secondTest["isReachable"]!.GetValue<bool>(), secondTest["message"]?.ToString());
            await Node(service, "device.delete", new { deviceId = secondId });

            // 7. The device's CLI and token usage are read through the installed helper.
            var cli = await Node(service, "cli.status", new { deviceId = "ssh:" + id });
            Log("7. 원격 CLI", cli.ToJsonString());
            Assert.Contains("2.1.0", cli.ToJsonString());
            var tokens = await Node(service, "tokens", null);
            var remoteTokens = (tokens["devices"] as JsonArray)!.OfType<JsonObject>().Single(d => d["id"]!.GetValue<string>() == id);
            Log("7. 원격 토큰 집계", remoteTokens.ToJsonString());
            Assert.True(remoteTokens["isReachable"]!.GetValue<bool>());
            Assert.Null(remoteTokens["error"]);

            // 8. Removing the device leaves the server's account as it is.
            await Node(service, "device.delete", new { deviceId = id });
            var snapshot = await Call<ManagementSnapshot>(service, "snapshot");
            Assert.DoesNotContain(snapshot.Devices, d => d.Kind == "ssh");
            Assert.Equal("live-account-1", RemoteAccount(remoteHome));
            Log("8. 장치 삭제", "목록에서 빠졌고 서버 계정은 그대로");
        }
        finally
        {
            if (service is not null)
            {
                await service.StopAsync();
                service.Dispose();
            }

            if (savedKnownHosts is null) File.Delete(knownHosts);
            else await File.WriteAllBytesAsync(knownHosts, savedKnownHosts);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void Log(string step, string detail) => output.WriteLine($"{step}: {detail}");

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

    private static string RemoteAccount(string remoteHome)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(remoteHome, ".claude.json")));
        return document.RootElement.GetProperty("oauthAccount").GetProperty("accountUuid").GetString()!;
    }

    // A usage endpoint that answers without the network; no Claude quota is in it.
    private sealed class UsageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }
}
