using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexSyncBar.Ubuntu.Backend;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Ubuntu.Tests;

public sealed class ManagementServiceIntegrationTests
{
    [Fact]
    public async Task UbuntuAccountAndPreviewLabelsUseFullEmailWhenAliasIsAbsent()
    {
        await using var fixture = new Fixture(withRemote: false);
        foreach (var profile in new[] { 1, 2 })
            await fixture.Call<object>("account.rename", new { profileId = profile, alias = "" });
        var snapshot = await fixture.Call<ManagementSnapshot>("snapshot");
        foreach (var account in snapshot.Accounts)
        {
            Assert.Equal($"fixture{account.Id}@example.invalid", account.Email);
            Assert.Equal(account.Email, account.Alias);
        }
        var preview = await fixture.Call<SwitchPreview>("preview", new { profileId = 2 });
        var target = Assert.Single(preview.Targets, item => item.Included);
        Assert.Equal("fixture1@example.invalid", target.CurrentAccountLabel);
        Assert.Equal("fixture2@example.invalid", target.TargetAccountLabel);
    }

    [Fact]
    public async Task CanonicalIdentityExportReturnsOnlyExpectedHashesWithoutChangingAuth()
    {
        await using var fixture = new Fixture(withRemote: false, mixedCaseEmail: true);
        await fixture.Call<ManagementSnapshot>("snapshot");
        var paths = new[] { fixture.Paths.ActiveAuthFile, fixture.Paths.ConfigurationFile,
            fixture.Paths.ProfileAuthFile(1), fixture.Paths.ProfileAuthFile(2) };
        var before = paths.ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        var identities = await fixture.Call<AccountIdentityHashes>("account.identityHashes");
        Assert.Equal(new[] { 1, 2 }, identities.Profiles.Select(profile => profile.ProfileId));
        foreach (var profile in identities.Profiles)
        {
            Assert.Equal(Hash("fixture-account-" + profile.ProfileId), profile.AccountIdSha256);
            Assert.Equal(Hash($"fixture{profile.ProfileId}@example.invalid"), profile.EmailSha256);
        }
        Assert.Equal(Hash(fixture.Paths.CodexHome), identities.CodexHomeSha256);
        Assert.False(string.IsNullOrWhiteSpace(identities.ConfigurationRevision));
        var json = JsonSerializer.Serialize(identities, Wire.Json);
        foreach (var raw in new[] { "fixture-account", "example.invalid", "synthetic-refresh", "header.", fixture.Paths.CodexHome })
            Assert.DoesNotContain(raw, json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "profiles", "configurationRevision", "codexHomeSha256" }, document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.All(document.RootElement.GetProperty("profiles").EnumerateArray(), profile =>
            Assert.Equal(new[] { "profileId", "accountIdSha256", "emailSha256" }, profile.EnumerateObject().Select(property => property.Name)));
        foreach (var path in paths) Assert.Equal(before[path], SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CanonicalIdentityExportRefusesAccountsRequiringLogin(bool pending, bool needsLogin)
    {
        await using var fixture = new Fixture(withRemote: false);
        await fixture.Call<ManagementSnapshot>("snapshot");
        var store = new ConfigurationStore(fixture.Paths);
        var configuration = store.LoadOrCreate();
        configuration.Accounts[1].IsPending = pending;
        configuration.Accounts[1].NeedsLogin = needsLogin;
        store.Save(configuration);
        var failure = await Assert.ThrowsAsync<RpcException>(() => fixture.Call<AccountIdentityHashes>("account.identityHashes"));
        Assert.Equal("account_unavailable", failure.Code);
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
    }

    [Fact]
    public async Task FailedNewLoginStillReportsItsReservedProfileAndPreservesActiveAuth()
    {
        // This fixture's codex executable exits before any URL/browser callback.
        await using var fixture = new Fixture(withRemote: true);
        var result = await fixture.Wait(await fixture.Call<OperationView>("account.add"));
        Assert.Equal("failed", result.State);
        Assert.Equal("login", result.Kind);
        Assert.Equal(3, result.ProfileId);
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
        Assert.DoesNotContain(new ConfigurationStore(fixture.Paths).LoadOrCreate().Accounts, account => account.Id == 3);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [Fact]
    public async Task ProductionLocalPreviewApplyAndDuplicateRequestRoundTripRealAuthFiles()
    {
        await using var fixture = new Fixture(withRemote: false);
        var before = await fixture.Call<ManagementSnapshot>("snapshot");
        Assert.False(before.IsDemo);
        Assert.Equal(1, before.ActiveProfileId);
        foreach (var target in new[] { 2, 1 })
        {
            var preview = await fixture.Call<SwitchPreview>("preview", new { profileId = target });
            Assert.True(preview.CanApply, preview.BlockingReason);
            Assert.Equal("fixture-account-" + (target == 2 ? 1 : 2), fixture.Auth.ReadActiveAccountId());
            var request = new { previewId = preview.PreviewId, requestId = Guid.NewGuid().ToString("N") };
            var operation = await fixture.Call<OperationView>("apply", request);
            var duplicate = await fixture.Call<OperationView>("apply", request);
            Assert.Equal(operation.Id, duplicate.Id);
            Assert.Equal("completed", (await fixture.Wait(operation)).State);
            Assert.Equal("fixture-account-" + target, fixture.Auth.ReadActiveAccountId());
            Assert.Equal(target, (await fixture.Call<ManagementSnapshot>("snapshot")).ActiveProfileId);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.Paths.ActiveAuthFile));
        }
    }

    [Fact]
    public async Task ProductionStalePreviewCannotWriteAfterAliasChanges()
    {
        await using var fixture = new Fixture(withRemote: false);
        var preview = await fixture.Call<SwitchPreview>("preview", new { profileId = 2 });
        await fixture.Call<object>("account.rename", new { profileId = 2, alias = "변경" });
        var result = await Assert.ThrowsAsync<RpcException>(() => fixture.Call<OperationView>("apply",
            new { previewId = preview.PreviewId, requestId = Guid.NewGuid().ToString("N") }));
        Assert.Equal("stale_preview", result.Code);
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
    }

    [Fact]
    public async Task ProductionSshBoundaryRunsRealHelperAndRollsBackVerificationFailure()
    {
        await using var fixture = new Fixture(withRemote: true);
        var preview = await fixture.Call<SwitchPreview>("preview", new { profileId = 2 });
        Assert.True(preview.CanApply, preview.BlockingReason);
        Assert.Equal(2, preview.Targets.Count(t => t.Included));
        var result = await fixture.Apply(preview);
        Assert.Equal("completed", result.State);
        Assert.Equal("fixture-account-2", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("fixture-account-2", fixture.RemoteAccount());
        using (var auth = JsonDocument.Parse(File.ReadAllText(fixture.RemoteAuth)))
            Assert.True(string.IsNullOrEmpty(auth.RootElement.GetProperty("tokens").GetProperty("refresh_token").GetString()));
        Assert.Equal("completed", (await fixture.Apply(await fixture.Call<SwitchPreview>("preview", new { profileId = 1 }))).State);

        // Failure after the remote write exercises real coordinator rollback on both sides.
        File.WriteAllText(fixture.FailVerification, "fail once");
        result = await fixture.Apply(await fixture.Call<SwitchPreview>("preview", new { profileId = 2 }));
        Assert.Equal("failed", result.State);
        Assert.All(result.Targets, target => Assert.Equal("restored", target.State));
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("fixture-account-1", fixture.RemoteAccount());
    }

    [Fact]
    public async Task ProductionPreviewStreamsBundledReadonlyHelperWithoutUsingInstalledOldHelper()
    {
        await using var fixture = new Fixture(withRemote: true);
        File.WriteAllText(fixture.RemoteHelper, "#!/bin/sh\nexit 91\n");
        var before = File.ReadAllText(fixture.RemoteAuth);
        var preview = await fixture.Call<SwitchPreview>("preview", new { profileId = 2 });
        Assert.True(preview.CanApply, preview.BlockingReason);
        Assert.True(File.Exists(fixture.StreamedStatus));
        Assert.Equal(before, File.ReadAllText(fixture.RemoteAuth));
        Assert.Equal("#!/bin/sh\nexit 91\n", File.ReadAllText(fixture.RemoteHelper));
    }

    [Fact]
    public async Task SingleSshAndLocalPreviewsChangeOnlyTheirExplicitTarget()
    {
        await using var fixture = new Fixture(withRemote: true);
        var preview = await fixture.Call<SwitchPreview>("preview", new { profileId = 2, deviceId = "ssh:fixture" });
        Assert.Equal("ssh:fixture", Assert.Single(preview.Targets, t => t.Included).Id);
        Assert.Equal("completed", (await fixture.Apply(preview)).State);
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("fixture-account-2", fixture.RemoteAccount());
        preview = await fixture.Call<SwitchPreview>("preview", new { profileId = 2, deviceId = "local" });
        Assert.Equal("local", Assert.Single(preview.Targets, t => t.Included).Id);
        Assert.Equal("completed", (await fixture.Apply(preview)).State);
        Assert.Equal("fixture-account-2", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("fixture-account-2", fixture.RemoteAccount());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuitDuringSwitchDrainsRollbackAndPreservesRecoveryResult(bool failRollback)
    {
        await using var fixture = new Fixture(withRemote: true);
        var preview = await fixture.Call<SwitchPreview>("preview", new { profileId = 2 });
        File.WriteAllText(fixture.BlockVerification, "pause after both targets were written");
        if (failRollback) File.WriteAllText(fixture.FailRestore, "reject synthetic remote restore");
        var accepted = await fixture.Call<OperationView>("apply", new
        {
            previewId = preview.PreviewId, requestId = Guid.NewGuid().ToString("N"),
        });
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(fixture.VerificationEntered))
        {
            Assert.True(DateTime.UtcNow < deadline, "Synthetic remote verification was not reached.");
            await Task.Delay(25);
        }
        Assert.Equal("fixture-account-2", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("fixture-account-2", fixture.RemoteAccount());
        Assert.True((await fixture.Call<ManagementSnapshot>("snapshot")).IsBusy);

        var response = JsonSerializer.SerializeToElement(await fixture.Call<object>("quit"), Wire.Json);
        Assert.True(response.GetProperty("shutdownRequested").GetBoolean());
        Assert.False(response.TryGetProperty("stopped", out _));
        Assert.True(fixture.ShutdownRequested);
        foreach (var method in new[] { "refresh", "account.rename" })
        {
            var rejected = await Assert.ThrowsAsync<RpcException>(() => fixture.Call<object>(method,
                new { profileId = 1, alias = "must not be saved" }));
            Assert.Equal("shutting_down", rejected.Code);
        }
        await fixture.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));

        var result = await fixture.Call<OperationView>("operation", new { operationId = accepted.Id });
        Assert.Equal(failRollback ? "recoveryRequired" : "failed", result.State);
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("fixture-account-" + (failRollback ? 2 : 1), fixture.RemoteAccount());
        Assert.Equal("restored", result.Targets.Single(target => target.Id == "local").State);
        Assert.Equal(failRollback ? "recoveryRequired" : "restored",
            result.Targets.Single(target => target.Id == "ssh:fixture").State);
        var persisted = new AccountSwitchCoordinator(fixture.Paths).ReadOperation();
        Assert.NotNull(persisted);
        Assert.Equal(result.State, persisted.State);
        Assert.Equal(accepted.Id, persisted.Id);
        Assert.Equal("계정1", new ConfigurationStore(fixture.Paths).LoadOrCreate().Accounts[0].CustomAlias);
    }

    [Fact]
    public async Task QuitRejectsMutationAlreadyWaitingForControllerLock()
    {
        await using var fixture = new Fixture(withRemote: false);
        await fixture.Call<ManagementSnapshot>("snapshot");
        var lease = await ControllerMutationLock.AcquireAsync(fixture.Paths);
        Task<object> mutation;
        try
        {
            mutation = fixture.Call<object>("account.rename", new { profileId = 1, alias = "too late" });
            Assert.False(mutation.IsCompleted);
            await fixture.Call<object>("quit");
        }
        finally { lease.Dispose(); }
        var rejected = await Assert.ThrowsAsync<RpcException>(() => mutation);
        Assert.Equal("shutting_down", rejected.Code);
        Assert.Equal("계정1", new ConfigurationStore(fixture.Paths).LoadOrCreate().Accounts[0].CustomAlias);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-production-integration-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> _previous = [];
        private readonly ManagementService _service;
        public WindowsPaths Paths { get; }
        public AuthStore Auth { get; }
        public string RemoteAuth => Path.Combine(_root, "remote", ".codex", "auth.json");
        public string FailVerification => Path.Combine(_root, "fail-verification");
        public string RemoteHelper => Path.Combine(_root, "remote", ".local", "bin", "gpt-switch");
        public string StreamedStatus => Path.Combine(_root, "streamed-status");
        public string BlockVerification => Path.Combine(_root, "block-verification");
        public string VerificationEntered => BlockVerification + ".entered";
        public string FailRestore => Path.Combine(_root, "fail-restore");
        public bool ShutdownRequested => _service.ShutdownRequested;
        public Task StopAsync() => _service.StopAsync();

        public Fixture(bool withRemote, bool mixedCaseEmail = false)
        {
            Paths = new(Path.Combine(_root, "home"), Path.Combine(_root, "data"), Path.Combine(_root, "runtime"));
            Paths.EnsureDirectories();
            Directory.CreateDirectory(Paths.RuntimeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var repository = FindRepository();
            foreach (var name in new[] { "gpt-switch", "codex-syncbar-askpass", "usage-summary.mjs" })
            {
                var destination = Path.Combine(Paths.RuntimeDirectory, name);
                File.Copy(Path.Combine(repository, "Support", name), destination);
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Auth = new(Paths);
            var accounts = new List<AccountProfile>();
            for (var id = 1; id <= 2; id++)
            {
                var email = mixedCaseEmail ? $"  FIXTURE{id}@Example.Invalid  " : $"fixture{id}@example.invalid";
                var claims = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { email, exp = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds() })).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var source = Path.Combine(Paths.LoginSessionsDirectory, $"source-{id}.json");
                WindowsPathSafety.WritePrivateBytes(source, JsonSerializer.SerializeToUtf8Bytes(new CodexAuthFile
                {
                    AuthMode = "chatgpt", Tokens = new() { AccountId = "fixture-account-" + id,
                        AccessToken = "header." + claims + ".signature", IdToken = "header." + claims + ".signature",
                        RefreshToken = "synthetic-refresh-" + id },
                }));
                Auth.ImportAuth(source, id);
                accounts.Add(new() { Id = id, Email = $"fixture{id}@example.invalid", CustomAlias = "계정" + id });
            }
            Auth.SwitchActive(1);
            var configuration = new AppConfiguration { Accounts = accounts, NextAccountId = 3 };
            if (withRemote)
            {
                PrepareRemote();
                configuration.Devices.Add(new() { Id = "fixture", DisplayName = "시험 서버", Host = "fixture.invalid", Username = "fixture", Authentication = "openSSHConfig", Enabled = true });
            }
            new ConfigurationStore(Paths).Save(configuration);
            _service = new(Paths, new UsageService(new HttpClient(new UsageHandler())));
        }

        private void PrepareRemote()
        {
            var remote = Path.Combine(_root, "remote");
            var profiles = Path.Combine(remote, ".local", "share", "gpt-switch", "profiles");
            foreach (var directory in new[] { profiles, Path.GetDirectoryName(RemoteAuth)!, Path.Combine(remote, ".local", "bin"), Path.Combine(_root, "bin") })
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            for (var id = 1; id <= 2; id++)
                WindowsPathSafety.WritePrivateBytes(Path.Combine(profiles, id + ".auth.json"), JsonSerializer.SerializeToUtf8Bytes(Auth.CreateAccessOnlyCopy(id)));
            WindowsPathSafety.WritePrivateBytes(RemoteAuth, JsonSerializer.SerializeToUtf8Bytes(Auth.CreateAccessOnlyCopy(1)));
            File.Copy(Paths.BundledGptSwitch, Path.Combine(remote, ".local", "bin", "gpt-switch"));
            var bin = Path.Combine(_root, "bin");
            Script(Path.Combine(bin, "pgrep"), "#!/bin/sh\nexit 1\n");
            Script(Path.Combine(bin, "codex"), "#!/bin/sh\n[ \"$1 $2\" = 'login status' ] && exit 0\nexit 90\n");
            Script(Path.Combine(bin, "ssh"), """
                #!/usr/bin/python3
                import os, pathlib, shlex, subprocess, sys, time
                remote = pathlib.Path(os.environ['SYNCBAR_FIXTURE_REMOTE'])
                command = sys.argv[-1]
                failure = pathlib.Path(os.environ['SYNCBAR_FIXTURE_FAIL'])
                blocked = pathlib.Path(os.environ['SYNCBAR_FIXTURE_BLOCK_VERIFY'])
                if command.endswith('__node verify 2') and blocked.exists():
                    pathlib.Path(str(blocked) + '.entered').write_text('verification reached')
                    while blocked.exists():
                        time.sleep(0.025)
                if command.endswith('__node switch 1 1') and pathlib.Path(os.environ['SYNCBAR_FIXTURE_FAIL_RESTORE']).exists():
                    raise SystemExit(45)
                if command.endswith('__node verify 2') and failure.exists():
                    failure.unlink()
                    raise SystemExit(44)
                environment = dict(os.environ, HOME=str(remote), CODEX_HOME=str(remote / '.codex'),
                                   GPT_SWITCH_STATE_ROOT=str(remote / '.local/share/gpt-switch'))
                environment.pop('GPT_SWITCH_CONFIG_FILE', None)
                arguments = shlex.split(command)
                if arguments == ['bash', '-l', '-s', '--', '__node', 'status-readonly']:
                    helper = sys.stdin.buffer.read()
                    assert b'status-readonly' in helper and b'node_status_readonly' in helper
                    pathlib.Path(os.environ['SYNCBAR_FIXTURE_STREAMED']).write_text('bundled helper received')
                    # Preserve this temporary HOME instead of sourcing the host login profile.
                    result = subprocess.run(['/bin/bash', '--noprofile', '--norc', '-s', '--',
                                             '__node', 'status-readonly'], input=helper, env=environment)
                    raise SystemExit(result.returncode)
                os.execve('/bin/bash', ['bash', '-c', command], environment)
                """ + "\n");
            Script(Path.Combine(bin, "scp"), """
                #!/usr/bin/python3
                import os, pathlib, shutil, sys
                remote = pathlib.Path(os.environ['SYNCBAR_FIXTURE_REMOTE'])
                relative = sys.argv[-1].split(':', 1)[1]
                assert relative.startswith('~/')
                destination = (remote / relative[2:]).resolve()
                assert destination.is_relative_to(remote.resolve())
                shutil.copyfile(sys.argv[-2], destination)
                """ + "\n");
            SetEnvironment("PATH", bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
            SetEnvironment("SYNCBAR_FIXTURE_REMOTE", remote);
            SetEnvironment("SYNCBAR_FIXTURE_FAIL", FailVerification);
            SetEnvironment("SYNCBAR_FIXTURE_STREAMED", StreamedStatus);
            SetEnvironment("SYNCBAR_FIXTURE_BLOCK_VERIFY", BlockVerification);
            SetEnvironment("SYNCBAR_FIXTURE_FAIL_RESTORE", FailRestore);
        }
        private static void Script(string path, string contents)
        {
            File.WriteAllText(path, contents);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        private void SetEnvironment(string key, string value)
        {
            _previous[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
        public string RemoteAccount()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(RemoteAuth));
            return document.RootElement.GetProperty("tokens").GetProperty("account_id").GetString()!;
        }
        public async Task<T> Call<T>(string method, object? parameters = null)
        {
            var result = await _service.DispatchAsync(method, JsonSerializer.SerializeToElement(parameters ?? new { }, Wire.Json), CancellationToken.None);
            return Assert.IsAssignableFrom<T>(result);
        }
        public async Task<OperationView> Apply(SwitchPreview preview) => await Wait(await Call<OperationView>("apply",
            new { previewId = preview.PreviewId, requestId = Guid.NewGuid().ToString("N") }));
        public async Task<OperationView> Wait(OperationView operation)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!operation.IsComplete)
            {
                Assert.True(DateTime.UtcNow < deadline, "Production operation timed out.");
                await Task.Delay(25);
                operation = await Call<OperationView>("operation", new { operationId = operation.Id });
            }
            // The public completion and service busy flag must agree before the next action.
            while ((await Call<ManagementSnapshot>("snapshot")).IsBusy)
            {
                Assert.True(DateTime.UtcNow < deadline);
                await Task.Delay(25);
            }
            return operation;
        }
        public async ValueTask DisposeAsync()
        {
            await _service.StopAsync();
            _service.Dispose();
            foreach (var pair in _previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        private static string FindRepository()
        {
            for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Support", "gpt-switch"))) return directory.FullName;
            throw new InvalidOperationException("Run from this repository.");
        }
    }
    private sealed class UsageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":25,"limit_window_seconds":18000}}}"""),
            });
    }
}
