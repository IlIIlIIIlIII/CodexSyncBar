using System.Text;
using System.Text.Json;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Runs the real POSIX helper in an isolated Linux/macOS home; covered by portable CI.";
    }
}

public sealed class WslHelperIntegrationTests
{
    [PosixFact]
    public async Task RealHelperBootstrapsAccessOnlyAuthSwitchesAndRestoresWithoutTouchingHost()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("POSIX integration test");
        var root = Path.Combine(Path.GetTempPath(), "syncbar-wsl-integration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var support = FindSupport();
            var paths = new WindowsPaths(Path.Combine(root, "windows-home"), Path.Combine(root, "windows-local"));
            var auth = new AuthStore(paths);
            var linuxHome = Path.Combine(root, "linux-home");
            var bin = Path.Combine(root, "test-bin");
            Directory.CreateDirectory(linuxHome);
            Directory.CreateDirectory(bin);
            // Process discovery is isolated too: never signal real user app-server processes.
            foreach (var command in new[] { "node", "ps", "pgrep" })
            {
                var file = Path.Combine(bin, command);
                await File.WriteAllTextAsync(file, "#!/bin/sh\nexit " + (command == "pgrep" ? "1" : "0") + "\n");
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            foreach (var id in new[] { 1, 2 })
            {
                var source = Path.Combine(root, "incoming.json");
                var claims = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"email\":\"test{id}@example.com\"}}"));
                await File.WriteAllTextAsync(source, JsonSerializer.Serialize(new CodexAuthFile
                {
                    AuthMode = "chatgpt", Tokens = new()
                    {
                        IdToken = "id." + claims + ".signature", AccessToken = "test-access-" + id,
                        RefreshToken = "test-refresh-" + id, AccountId = "test-account-" + id,
                    },
                }));
                File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                auth.ImportAuth(source, id);
            }
            var runner = new BashRunner(linuxHome, bin);
            var service = new WslDeviceService(paths, auth, runner, support);
            var device = await service.BootstrapAsync(new() { Distribution = "IsolatedUbuntu" },
                [new() { Id = 1 }, new() { Id = 2 }], 1);
            Assert.True(device.Enabled);
            var activeFile = Path.Combine(linuxHome, ".codex", "auth.json");
            using (var active = JsonDocument.Parse(await File.ReadAllTextAsync(activeFile)))
            {
                Assert.Equal("test-account-1", active.RootElement.GetProperty("tokens").GetProperty("account_id").GetString());
                Assert.True(!active.RootElement.GetProperty("tokens").TryGetProperty("refresh_token", out var refresh)
                    || string.IsNullOrEmpty(refresh.GetString()));
            }
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(activeFile));
            var target = service.CreateAccountTarget(device);
            var checkpoint = await target.PrepareAsync(2, CancellationToken.None);
            await target.ApplyAsync(2, CancellationToken.None);
            await target.VerifyAsync(2, CancellationToken.None);
            await target.RestoreAsync(checkpoint, CancellationToken.None);
            await target.VerifyAsync(1, CancellationToken.None);

            var logout = service.CreateLogoutTarget(device, 1, 2);
            var logoutCheckpoint = await logout.PrepareAsync(2, CancellationToken.None);
            await logout.ApplyAsync(2, CancellationToken.None);
            await logout.VerifyAsync(2, CancellationToken.None);
            await logout.RestoreAsync(logoutCheckpoint, CancellationToken.None);
            await target.VerifyAsync(1, CancellationToken.None);

            // Model the durable state left by a hard kill after the new account was applied.
            // Recovery uses the real helper's home, but the controller journal remains on Windows.
            var pending = Path.Combine(paths.StateRoot, "wsl-bootstrap-pending.json");
            var interrupted = await runner.RunAsync(["--distribution", "IsolatedUbuntu", "--exec", "bash", "-lc", """
                set -eu
                umask 077
                backup="$HOME/.local/share/.syncbar-wsl-bootstrap"
                mkdir "$backup"
                cp -a "$HOME/.local/share/gpt-switch" "$backup/state"
                cp -p "$HOME/.codex/auth.json" "$backup/auth.json"
                : > "$backup/state-existed"
                : > "$backup/auth-existed"
                : > "$backup/ready"
                "$HOME/.local/bin/gpt-switch" __node switch 2 0
                """], null, CancellationToken.None);
            Assert.Equal(0, interrupted.ExitCode);
            await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(device));
            var recovered = await new WslDeviceService(paths, auth, runner, support).RecoverPendingBootstrapTransactionsAsync();
            Assert.Empty(recovered);
            Assert.False(File.Exists(pending));
            await target.VerifyAsync(1, CancellationToken.None);
            Assert.DoesNotContain(runner.Calls, call => call.Any(argument => argument.Contains("test-refresh-", StringComparison.Ordinal)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static string FindSupport()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Support");
            if (File.Exists(Path.Combine(candidate, "gpt-switch"))) return candidate;
        }
        throw new InvalidOperationException("Run from the repository to locate Support/gpt-switch.");
    }

    private sealed class BashRunner(string isolatedHome, string bin) : IWslCommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string? input, CancellationToken cancellationToken)
        {
            Calls.Add(arguments.ToArray());
            if (arguments[0] == "--list") return new ProcessResult(0, "IsolatedUbuntu", "");
            Assert.Equal("--distribution", arguments[0]);
            var result = await ProcessRunner.RunAsync("/bin/bash", ["-c", arguments[^1]], input, cancellationToken,
                timeout: TimeSpan.FromSeconds(25), environment: new Dictionary<string, string?>
                {
                    ["HOME"] = isolatedHome,
                    ["CODEX_HOME"] = Path.Combine(isolatedHome, ".codex"),
                    ["GPT_SWITCH_STATE_ROOT"] = Path.Combine(isolatedHome, ".local", "share", "gpt-switch"),
                    ["GPT_SWITCH_CONFIG_FILE"] = null,
                    ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                });
            Assert.True(result.ExitCode == 0, "Isolated helper failed: " + result.StandardError);
            return result;
        }
    }
}
