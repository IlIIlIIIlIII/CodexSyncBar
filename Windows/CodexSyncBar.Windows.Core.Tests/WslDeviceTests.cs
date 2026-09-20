using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class WslDeviceTests
{
    [Fact]
    public void DiscoveryNormalizesWslOutputAndExcludesDocker()
    {
        var output = "U\0b\0u\0n\0t\0u\0\r\0\n\0docker-desktop\r\ndocker-desktop-data\r\nUbuntu\r\nDebian\r\n";
        Assert.Equal(new[] { "Ubuntu", "Debian" }, WslDeviceService.ParseDistributionList(output));
    }

    [Theory]
    [InlineData("docker-desktop")]
    [InlineData("--exec")]
    [InlineData("Ubuntu\nexec")]
    public void InvalidDistributionIsRejected(string name) =>
        Assert.Throws<CodexSyncBarException>(() => WslDeviceService.ValidateDistribution(name));

    [Fact]
    public async Task CliInspectionDoesNotStartStoppedDistribution()
    {
        using var fixture = new Fixture();
        fixture.Runner.Running = "";
        var result = CliInstallation.Parse(await fixture.Service.RunCodexHelperAsync(new() { Distribution = "Ubuntu" }, false));
        Assert.True(result.CanUpdate);
        Assert.Contains("정지", result.Notice);
        Assert.All(fixture.Runner.Calls, arguments => Assert.Equal("--list", arguments[0]));
    }

    [Fact]
    public async Task BackgroundStatusNeverStartsStoppedDistro()
    {
        using var fixture = new Fixture();
        fixture.Store.Save([new() { Distribution = "Ubuntu", Username = "user", HomeDirectory = "/home/user", Enabled = true, IsInstalled = true }]);
        fixture.Runner.Running = "";
        var statuses = await fixture.Service.FetchStatusesAsync();
        Assert.Single(statuses);
        Assert.Equal("stopped", statuses[0].CliState);
        Assert.All(fixture.Runner.Calls, arguments => Assert.Equal("--list", arguments[0]));
    }

    [Fact]
    public async Task ExplicitApplyPreflightMayStartEnabledDistroUsingSeparateArguments()
    {
        using var fixture = new Fixture();
        fixture.AddProfile(2);
        var device = new WslDeviceConfiguration { Distribution = "Ubuntu Work", Username = "user", HomeDirectory = "/home/user", Enabled = true, IsInstalled = true };
        var previous = await fixture.Service.CreateAccountTarget(device).PrepareAsync(2, CancellationToken.None);
        Assert.Equal("1", previous);
        Assert.All(fixture.Runner.Calls, arguments =>
        {
            Assert.Equal("--distribution", arguments[0]);
            Assert.Equal("Ubuntu Work", arguments[1]);
            Assert.Equal("--user", arguments[2]);
            Assert.Equal("user", arguments[3]);
        });
    }

    [Fact]
    public void UninstalledDistributionCannotBeEnabled()
    {
        using var fixture = new Fixture();
        Assert.Throws<CodexSyncBarException>(() => fixture.Store.Save([new() { Distribution = "Ubuntu", Enabled = true }]));
        Assert.False(File.Exists(fixture.Store.FilePath));
    }

    [Fact]
    public void WslConfigurationIsSeparateFromSharedAccountConfiguration()
    {
        using var fixture = new Fixture();
        var shared = new ConfigurationStore(fixture.Paths);
        shared.LoadOrCreate();
        var before = File.ReadAllText(fixture.Paths.ConfigurationFile);
        fixture.Store.Save([new() { Distribution = "Ubuntu" }]);
        Assert.Equal(before, File.ReadAllText(fixture.Paths.ConfigurationFile));
        Assert.Single(fixture.Store.Load());
    }

    [Fact]
    public void CredentialFingerprintsMatchTheTwelveCharacterHelperProtocol()
    {
        Assert.Equal("ba7816bf8f01", WslDeviceService.CredentialFingerprint("abc"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-wsl-tests-" + Guid.NewGuid().ToString("N"));
        public WindowsPaths Paths { get; }
        public WslConfigurationStore Store { get; }
        public RecordingRunner Runner { get; } = new();
        public WslDeviceService Service { get; }
        public Fixture()
        {
            Paths = new(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
            Store = new(Paths);
            Service = new(Paths, new AuthStore(Paths), Runner);
        }
        public void AddProfile(int profile)
        {
            Directory.CreateDirectory(_root);
            var source = Path.Combine(_root, "incoming.json");
            var claims = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"email\":\"test@example.com\"}"));
            WindowsPathSafety.WritePrivateBytes(source, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new CodexAuthFile
            {
                AuthMode = "chatgpt", Tokens = new CodexTokens
                {
                    IdToken = "test." + claims + ".signature", AccessToken = "test-access", RefreshToken = "test-refresh", AccountId = "test-account",
                },
            }));
            new AuthStore(Paths).ImportAuth(source, profile);
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }

    private sealed class RecordingRunner : IWslCommandRunner
    {
        public string Running { get; set; } = "Ubuntu";
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string? input, CancellationToken cancellationToken)
        {
            Calls.Add(arguments.ToArray());
            var output = arguments[0] == "--list" ? arguments.Contains("--running") ? Running : "Ubuntu"
                : arguments.Last().Contains("'version'") ? WslDeviceService.HelperVersion : "active=1 fingerprint=example cli=ready";
            return Task.FromResult(new ProcessResult(0, output, ""));
        }
    }
}
