using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class CoreContractTests
{
    [Fact]
    public async Task CommandScriptArgumentsSurviveCmdEscaping()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"codex syncbar cmd test {Guid.NewGuid():N}");
        var script = Path.Combine(root, "echo args.cmd");
        try
        {
            Directory.CreateDirectory(root);
            var echoScript = Path.Combine(root, "echo args.cjs");
            await File.WriteAllTextAsync(
                echoScript,
                "process.stdout.write(JSON.stringify(process.argv.slice(2)));\n");
            await File.WriteAllTextAsync(
                script,
                $"@echo off\r\nnode \"{echoScript}\" %*\r\n");

            string[] arguments = [
                "alpha beta",
                "100%PATH% & safe",
                "a\"b",
                "bang!value",
                "caret^value",
                "trailing\\",
                "pipe|value",
                "meta&<>()value",
                "",
                "slash\\\"quote",
                "two\\\\",
            ];
            var result = await ProcessRunner.RunAsync(
                script,
                arguments,
                timeout: TimeSpan.FromSeconds(10));
            Assert.Equal(0, result.ExitCode);
            using var output = System.Text.Json.JsonDocument.Parse(result.StandardOutput);
            var actual = output.RootElement
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            Assert.Equal(arguments, actual);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CommandScriptStandardInputRoundTripsThroughNativeLaunch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"codex syncbar cmd stdin {Guid.NewGuid():N}");
        var script = Path.Combine(root, "stdin.cmd");
        const string input = "stdin with % symbols, ! marks, and UTF-8 한글\n";
        try
        {
            Directory.CreateDirectory(root);
            var echoScript = Path.Combine(root, "stdin.cjs");
            await File.WriteAllTextAsync(
                echoScript,
                "let value = ''; process.stdin.setEncoding('utf8'); process.stdin.on('data', chunk => value += chunk); process.stdin.on('end', () => process.stdout.write(value));\n");
            await File.WriteAllTextAsync(
                script,
                $"@echo off\r\nnode \"{echoScript}\" %*\r\n");

            var result = await ProcessRunner.RunAsync(
                script,
                [],
                standardInput: input,
                timeout: TimeSpan.FromSeconds(10));

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(input, result.StandardOutput);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CommandScriptEnvironmentOverrideReachesTheChildProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"codex syncbar cmd environment {Guid.NewGuid():N}");
        var home = Path.Combine(root, "login home with spaces");
        var marker = Path.Combine(home, "marker.txt");
        var script = Path.Combine(root, "check environment.cmd");
        try
        {
            Directory.CreateDirectory(home);
            await File.WriteAllTextAsync(marker, "present");
            await File.WriteAllTextAsync(
                script,
                "@echo off\r\nif exist \"%CODEX_HOME%\\marker.txt\" (echo exists) else (echo missing)\r\n");

            var result = await ProcessRunner.RunAsync(
                script,
                [],
                environment: new Dictionary<string, string?>
                {
                    ["CODEX_HOME"] = home,
                });

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("exists", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InstalledCodexCommandAcceptsTheLoginHomeEnvironment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var codex = CodexCliLocator.Find();
        if (codex is null || !File.Exists(codex))
        {
            return;
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex-syncbar",
            "LoginSessions",
            $"test-{Guid.NewGuid():N}");
        CommandProcess? process = null;
        try
        {
            Directory.CreateDirectory(root);
            process = ProcessRunner.StartInteractive(
                codex,
                [
                    "app-server",
                    "--stdio",
                    "-c",
                    "cli_auth_credentials_store=\"file\"",
                ],
                redirectStandardInput: true,
                workingDirectory: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                environment: new Dictionary<string, string?>
                {
                    ["CODEX_HOME"] = root,
                    ["NO_COLOR"] = "1",
                });

            await process.StandardInput.WriteLineAsync(
                "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex-syncbar-test\",\"title\":\"Codex SyncBar test\",\"version\":\"1.0.0\"},\"capabilities\":{}}}");
            await process.StandardInput.FlushAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = await process.StandardOutput.ReadLineAsync(timeout.Token);

            Assert.NotNull(response);
            using var document = System.Text.Json.JsonDocument.Parse(response!);
            var codexHome = document.RootElement
                .GetProperty("result")
                .GetProperty("codexHome")
                .GetString();
            Assert.Equal(root, codexHome);
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch
                {
                }

                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }

                    using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(exitTimeout.Token);
                }
                catch
                {
                }

                process.Dispose();
            }

            if (Directory.Exists(root))
            {
                for (var attempt = 0; attempt < 20 && Directory.Exists(root); attempt++)
                {
                    try
                    {
                        Directory.Delete(root, recursive: true);
                    }
                    catch (IOException) when (attempt < 19)
                    {
                        await Task.Delay(100);
                    }
                }
            }
        }
    }

    [Fact]
    public void UsagePayloadKeepsFiveHourAndWeeklyWindowsSeparated()
    {
        var credentials = new ProfileCredentials(
            7,
            "access",
            "id",
            "refresh",
            "account",
            "person@example.com",
            null,
            "profile.auth.json",
            true);
        const string json = """
            {
              "plan_type": "pro",
              "rate_limit": {
                "primary_window": { "used_percent": 35, "reset_at": 1900000000, "limit_window_seconds": 18000 },
                "secondary_window": { "used_percent": 80, "reset_at": 1900500000, "limit_window_seconds": 604800 }
              },
              "credits": { "unlimited": false, "balance": "12.5" },
              "additional_rate_limits": [
                {
                  "limit_name": "Spark",
                  "rate_limit": {
                    "primary_window": { "used_percent": 10, "limit_window_seconds": 18000 }
                  }
                }
              ],
              "rate_limit_reset_credits": {
                "available_count": 2,
                "credits": [{ "expires_at": "2030-01-01T00:00:00Z" }]
              }
            }
            """;

        var snapshot = UsageService.ParseUsagePayload(json, credentials);

        Assert.Equal(7, snapshot.ProfileId);
        Assert.Equal("pro", snapshot.Plan);
        Assert.Equal(35, snapshot.Session!.UsedPercent);
        Assert.Equal(80, snapshot.Weekly!.UsedPercent);
        Assert.Equal(2, snapshot.ResetCredits);
        Assert.Equal(12.5, snapshot.CreditBalance);
        Assert.Equal(90, snapshot.SparkSession!.RemainingPercent);
    }

    [Fact]
    public void ConfigurationStorePersistsTheMacCompatibleSchema()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-test-{Guid.NewGuid():N}");
        try
        {
            var paths = new WindowsPaths(
                Path.Combine(root, "home"),
                Path.Combine(root, "local"));
            var store = new ConfigurationStore(paths);
            var configuration = store.LoadOrCreate();
            var account = configuration.Accounts.Single();

            store.UpdateAccountEmail(configuration, account.Id, "person@example.com");
            store.UpdateAccountAlias(configuration, account.Id, "Main");

            var reloaded = store.LoadOrCreate();
            Assert.Equal("person@example.com", reloaded.Accounts.Single().Email);
            Assert.Equal("Main", reloaded.Accounts.Single().CustomAlias);

            var json = File.ReadAllText(paths.ConfigurationFile);
            Assert.Contains("\"nextAccountID\"", json, StringComparison.Ordinal);
            Assert.Contains("\"isPending\"", json, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void LoggedOutAccountRemainsAvailableForReauthentication()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-logout-state-{Guid.NewGuid():N}");
        try
        {
            var paths = new WindowsPaths(
                Path.Combine(root, "home"),
                Path.Combine(root, "local"));
            var store = new ConfigurationStore(paths);
            var configuration = store.LoadOrCreate();
            var account = store.ReserveAccount(configuration);
            store.UpdateAccountEmail(configuration, account.Id, "person@example.com");

            store.MarkAccountLoggedOut(configuration, account.Id);

            var reloaded = store.LoadOrCreate();
            var loggedOut = reloaded.Accounts.Single(item => item.Id == account.Id);
            Assert.False(loggedOut.IsPending);
            Assert.True(loggedOut.NeedsLogin);
            Assert.Equal("로그아웃됨 · 재로그인 필요", loggedOut.StateLabel);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DisplayOnlyDeviceEditsReuseCredentialNamespace()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-secret-test-{Guid.NewGuid():N}");
        try
        {
            var paths = new WindowsPaths(
                Path.Combine(root, "home"),
                Path.Combine(root, "local"));
            var configurationStore = new ConfigurationStore(paths);
            var configuration = configurationStore.LoadOrCreate();
            var authStore = new AuthStore(paths);
            var service = new SshDeviceService(authStore, paths);
            var device = new SshDeviceConfiguration
            {
                Id = "dev-server",
                DisplayName = "Dev server",
                Host = "dev.example.com",
                Port = 22,
                Username = "developer",
                Authentication = "password",
                Enabled = false,
            };

            var first = service.PrepareForSave(configuration, device, "first-secret", "");
            configurationStore.UpsertDevice(configuration, first.Device);
            var secretStore = new WindowsSecretStore(paths);
            Assert.Equal(
                "first-secret",
                secretStore.Read($"{first.Device.CredentialId:D}.password"));

            var edited = DeviceConfigurationComparer.Clone(first.Device);
            edited.DisplayName = "Renamed server";
            var second = service.PrepareForSave(configuration, edited, "", "");

            Assert.Equal(first.Device.CredentialId, second.Device.CredentialId);
            Assert.Equal(
                "first-secret",
                secretStore.Read($"{second.Device.CredentialId:D}.password"));
            Assert.Null(second.ReplacedCredentialId);
            Assert.False(second.Device.Enabled);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void WeeklyAnchorDecisionHonorsRetryAndFutureReset()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new UsageWindow(0, now.AddHours(4), 604800);
        var record = new WeeklyAnchorRecord();

        Assert.Equal(
            WeeklyAnchorDecision.Trigger,
            WeeklyAnchorDecisionEngine.Decide(true, window, record, now));

        record.LastAttemptAt = now;
        Assert.Equal(
            WeeklyAnchorDecision.None,
            WeeklyAnchorDecisionEngine.Decide(true, window, record, now.AddMinutes(5)));

        record.LastAttemptAt = null;
        record.NextResetAt = now.AddHours(2);
        Assert.Equal(
            WeeklyAnchorDecision.ConfirmResetDrift,
            WeeklyAnchorDecisionEngine.Decide(true, window, record, now));
    }

    [Fact]
    public void WeeklyAnchorDecisionMatchesMacRetryAndDriftRules()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new UsageWindow(0, now.AddHours(4), 604800);
        var record = new WeeklyAnchorRecord
        {
            NextResetAt = now.AddHours(1),
            ResetDriftCandidateAt = now.AddHours(3),
            ResetDriftObservationCount = 1,
            LastAttemptAt = now.AddMinutes(-5),
            LastError = "ordinary failure",
        };

        Assert.Equal(
            WeeklyAnchorDecision.None,
            WeeklyAnchorDecisionEngine.Decide(true, window, record, now));

        record.LastError = "Reading additional input from stdin";
        Assert.Equal(
            WeeklyAnchorDecision.Trigger,
            WeeklyAnchorDecisionEngine.Decide(true, window, record, now));

        record.LastAttemptAt = null;
        record.ResetDriftObservationCount = 0;
        record.ResetDriftCandidateAt = null;
        Assert.Equal(
            WeeklyAnchorDecision.ConfirmResetDrift,
            WeeklyAnchorDecisionEngine.Decide(true, window, record, now));
    }

    [Fact]
    public void UsageFormattingMatchesResetAndCreditExpiryContracts()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("2일 3시간 후 초기화", UsageFormatting.ResetDescription(now.AddDays(2).AddHours(3), now));
        Assert.Equal("5시간 30분", UsageFormatting.ResetCreditExpiryDescription(now.AddHours(5.5), now));
        Assert.Equal("2일 3시간", UsageFormatting.ResetCreditExpiryDescription(now.AddDays(2).AddHours(3), now));
        Assert.Equal(
            "다음 만료 5시간 30분 · 외 1회",
            UsageFormatting.CompactResetCreditExpiryDescription(
                [now.AddHours(5.5), now.AddHours(7)],
                now));
    }

    [Fact]
    public void MenuTitlePreservesStatusSignalsAndLowQuotaWarning()
    {
        var account = new AccountProfile { Id = 1, Email = "person@example.com", CustomAlias = "Main" };
        var snapshot = new UsageSnapshot(
            1,
            account.Email,
            "Pro",
            new UsageWindow(95, DateTimeOffset.UtcNow.AddHours(1), 3600),
            null,
            null,
            null,
            null,
            false,
            null,
            [],
            DateTimeOffset.UtcNow);

        Assert.Equal(
            "⚠ Main 5%",
            MenuTitleFormatter.Title(
                account,
                snapshot,
                null,
                [UsageDisplayItem.FiveHour],
                false,
                false));

        account.NeedsLogin = true;
        Assert.Equal(
            "Main 🔒",
            MenuTitleFormatter.Title(account, snapshot, null, [UsageDisplayItem.FiveHour], false, false));
    }

    [Fact]
    public void DeviceActivationIntentRollsBackAnEnabledConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-activation-test-{Guid.NewGuid():N}");
        try
        {
            var paths = new WindowsPaths(
                Path.Combine(root, "home"),
                Path.Combine(root, "local"));
            var configurationStore = new ConfigurationStore(paths);
            var configuration = configurationStore.LoadOrCreate();
            var device = new SshDeviceConfiguration
            {
                Id = "build-server",
                DisplayName = "Build server",
                Host = "build.example.com",
                Username = "builder",
                Authentication = "openSSHConfig",
                Enabled = false,
            };
            configurationStore.UpsertDevice(configuration, device);

            var transactions = new DeviceActivationTransactionStore(paths);
            var intent = transactions.Save(device);
            configurationStore.BeginDeviceActivation(configuration, device);
            Assert.True(configuration.Devices.Single().Enabled);

            transactions.Recover(configuration, configurationStore);

            Assert.False(configuration.Devices.Single().Enabled);
            Assert.False(File.Exists(intent));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void LoginImportReplacesTheActiveProfileAndLeavesNoTransaction()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-login-test-{Guid.NewGuid():N}");
        try
        {
            var paths = new WindowsPaths(
                Path.Combine(root, "home"),
                Path.Combine(root, "local"));
            var configurationStore = new ConfigurationStore(paths);
            var configuration = configurationStore.LoadOrCreate();
            var account = configurationStore.ReserveAccount(configuration);
            var authStore = new AuthStore(paths);
            var oldPath = Path.Combine(root, "old.auth.json");
            var newPath = Path.Combine(root, "new.auth.json");
            File.WriteAllText(oldPath, FullAuthJson("old-account", "old@example.com"));
            File.WriteAllText(newPath, FullAuthJson("new-account", "new@example.com"));

            authStore.ImportAuth(oldPath, account.Id);
            authStore.SwitchActive(account.Id);
            var transactions = new LoginTransactionStore(paths);

            transactions.ImportAuth(authStore, newPath, account.Id, replaceExisting: true);

            Assert.Equal("new-account", authStore.ReadCredentials(account.Id).AccountId);
            Assert.Equal("new-account", authStore.ReadActiveAccountId());
            Assert.Empty(Directory.EnumerateDirectories(paths.LoginTransactionsDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DiscoverExistingAccountsRepairsEmailAfterInterruptedReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-login-recovery-{Guid.NewGuid():N}");
        try
        {
            var paths = new WindowsPaths(
                Path.Combine(root, "home"),
                Path.Combine(root, "local"));
            var configurationStore = new ConfigurationStore(paths);
            var configuration = configurationStore.LoadOrCreate();
            var account = configurationStore.ReserveAccount(configuration);
            configurationStore.UpdateAccountEmail(configuration, account.Id, "old@example.com");
            var authStore = new AuthStore(paths);
            var authPath = Path.Combine(root, "replacement.auth.json");
            File.WriteAllText(authPath, FullAuthJson("new-account", "new@example.com"));
            authStore.ImportAuth(authPath, account.Id, replaceExisting: true);

            configurationStore.DiscoverExistingAccounts(configuration, authStore);

            var repaired = configuration.Accounts.Single(item => item.Id == account.Id);
            Assert.Equal("new@example.com", repaired.Email);
            Assert.False(repaired.NeedsLogin);
            Assert.False(repaired.IsPending);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DisabledDeviceCannotBeEnabledWithoutActivationFlow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-device-enable-{Guid.NewGuid():N}");
        try
        {
            var paths = new WindowsPaths(
                Path.Combine(root, "home"),
                Path.Combine(root, "local"));
            var configurationStore = new ConfigurationStore(paths);
            var configuration = configurationStore.LoadOrCreate();
            var authStore = new AuthStore(paths);
            var service = new SshDeviceService(authStore, paths);
            var device = new SshDeviceConfiguration
            {
                Id = "staging-server",
                DisplayName = "Staging server",
                Host = "staging.example.com",
                Username = "developer",
                Authentication = "openSSHConfig",
                Enabled = false,
            };
            configurationStore.UpsertDevice(configuration, device);

            var edited = DeviceConfigurationComparer.Clone(device);
            edited.Enabled = true;

            var error = Assert.Throws<CodexSyncBarException>(() =>
                service.PrepareForSave(configuration, edited, "", ""));
            Assert.Contains("설치 및 활성화", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void RemoteBootstrapJournalBindsArchiveToOneEndpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-bootstrap-journal-{Guid.NewGuid():N}");
        try
        {
            var paths = new WindowsPaths(
                Path.Combine(root, "home"),
                Path.Combine(root, "local"));
            paths.EnsureDirectories();
            var store = new RemoteBootstrapTransactionStore(paths);
            var endpoint = new string('a', 64);
            var archive = new byte[] { 0, 1, 2, 3, 4 };

            var transaction = store.Begin("build-server", endpoint, archive);
            var loaded = Assert.Single(store.LoadAll());

            Assert.Equal(transaction.Operation, loaded.Operation);
            Assert.Equal(endpoint, loaded.EndpointSha256);
            Assert.Equal(archive, store.ReadArchive(loaded));
            Assert.Throws<CodexSyncBarException>(() =>
                store.Begin("build-server", endpoint, archive));

            store.Delete(loaded);
            Assert.Empty(store.LoadAll());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void EndpointChangeCannotSilentlyReuseStoredKeyPassphrase()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-passphrase-endpoint-{Guid.NewGuid():N}");
        try
        {
            var paths = new WindowsPaths(
                Path.Combine(root, "home"),
                Path.Combine(root, "local"));
            var configurationStore = new ConfigurationStore(paths);
            var configuration = configurationStore.LoadOrCreate();
            var authStore = new AuthStore(paths);
            var service = new SshDeviceService(authStore, paths);
            var device = new SshDeviceConfiguration
            {
                Id = "key-server",
                DisplayName = "Key server",
                Host = "old.example.com",
                Username = "developer",
                Authentication = "privateKey",
                IdentityFile = Path.Combine(root, "id_ed25519"),
                Enabled = false,
            };
            File.WriteAllText(device.IdentityFile!, "private-key");

            var first = service.PrepareForSave(configuration, device, "", "old-passphrase");
            configurationStore.UpsertDevice(configuration, first.Device);

            var edited = DeviceConfigurationComparer.Clone(first.Device);
            edited.Host = "new.example.com";

            var error = Assert.Throws<CodexSyncBarException>(() =>
                service.PrepareForSave(configuration, edited, "", ""));
            Assert.Contains("키 암호를 다시 입력", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void SelectedProfilePreferenceRoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-selected-profile-{Guid.NewGuid():N}");
        try
        {
            var paths = new WindowsPaths(
                Path.Combine(root, "home"),
                Path.Combine(root, "local"));
            var store = new SelectedProfileStore(paths);

            Assert.Null(store.Load());
            store.Save(17);

            Assert.Equal(17, store.Load());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void OnlyCodexAppServerCommandLinesAreRestartable()
    {
        Assert.True(LocalSwitchService.IsCodexAppServerCommandLine(
            "codex.exe",
            @"C:\\Program Files\\Codex\\codex.exe app-server proxy"));
        Assert.True(LocalSwitchService.IsCodexAppServerCommandLine(
            "node.exe",
            @"node.exe C:\\Tools\\codex.js app-server --listen unix://127.0.0.1"));
        Assert.False(LocalSwitchService.IsCodexAppServerCommandLine(
            "codex.exe",
            @"C:\\Program Files\\Codex\\codex.exe exec --model gpt-5"));
        Assert.False(LocalSwitchService.IsCodexAppServerCommandLine(
            "codex.exe",
            @"C:\\Program Files\\Codex\\codex.exe app-server --stdio"));
    }

    [Fact]
    public void MissingReservedProfileDoesNotBlockActiveProfileDiscovery()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-startup-{Guid.NewGuid():N}");
        try
        {
            var paths = new WindowsPaths(
                Path.Combine(root, "home"),
                Path.Combine(root, "local"));
            var configuration = new ConfigurationStore(paths).LoadOrCreate();
            var authStore = new AuthStore(paths);
            authStore.RestoreActive(new CodexAuthFile
            {
                AuthMode = "chatgpt",
                Tokens = new CodexTokens
                {
                    IdToken = "id-token",
                    AccessToken = "access-token",
                    RefreshToken = "refresh-token",
                    AccountId = "active-account",
                },
            });

            var activeProfileId = new LocalSwitchService(authStore, paths)
                .GetActiveProfileId(configuration.Accounts);

            Assert.Null(activeProfileId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void CodexCliLocatorPrefersTheWindowsCommandShim()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-cli-locator-{Guid.NewGuid():N}");
        try
        {
            var desktopDirectory = Path.Combine(root, "desktop");
            var cliDirectory = Path.Combine(root, "cli");
            Directory.CreateDirectory(desktopDirectory);
            Directory.CreateDirectory(cliDirectory);
            File.WriteAllText(Path.Combine(desktopDirectory, "codex.exe"), string.Empty);
            File.WriteAllText(Path.Combine(cliDirectory, "codex.cmd"), string.Empty);

            var selected = CodexCliLocator.FindInPath(
                string.Join(Path.PathSeparator, desktopDirectory, cliDirectory));

            Assert.Equal(Path.Combine(cliDirectory, "codex.cmd"), selected);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void LoginSessionsAvoidPackagedLocalAppDataVirtualization()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-paths-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var localAppData = Path.Combine(root, "local");
        var paths = new WindowsPaths(home, localAppData);

        Assert.Equal(
            Path.Combine(Path.GetFullPath(home), ".codex-syncbar", "LoginSessions"),
            paths.LoginSessionsDirectory);
        Assert.False(
            paths.LoginSessionsDirectory.StartsWith(
                Path.GetFullPath(localAppData) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string FullAuthJson(string accountId, string email)
    {
        var claims = $"{{\"email\":\"{email}\"}}";
        return System.Text.Json.JsonSerializer.Serialize(new CodexAuthFile
        {
            AuthMode = "chatgpt",
            Tokens = new CodexTokens
            {
                IdToken = $"id.{Base64Url(claims)}.sig",
                AccessToken = $"access-{accountId}",
                RefreshToken = $"refresh-{accountId}",
                AccountId = accountId,
            },
        });
    }

    private static string Base64Url(string value) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
