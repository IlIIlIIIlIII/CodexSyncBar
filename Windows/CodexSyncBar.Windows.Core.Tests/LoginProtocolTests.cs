using System.Text;
using System.Text.Json;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class LoginProtocolTests
{
    private const string Initialized = """{"id":1,"result":{}}""";
    private const string Started = """{"id":2,"result":{"type":"chatgpt","authUrl":"https://auth.openai.com/oauth/authorize","loginId":"current"}}""";
    private const string Updated = """{"method":"account/updated","params":{"authMode":"chatgpt"}}""";
    private const string Completed = """{"method":"account/login/completed","params":{"loginId":"current","success":true}}""";
    private const string Account = """{"id":3,"result":{"account":{"type":"chatgpt","email":"test@example.com"}}}""";
    private const string Limits = """{"id":4,"result":{"rateLimits":{}}}""";

    [Theory]
    [InlineData("{\"loginId\":\"old\",\"success\":false,\"error\":\"old failed login\"}")]
    [InlineData("{\"loginId\":\"old\",\"success\":true}")]
    [InlineData("{\"success\":true}")]
    [InlineData("{\"loginId\":null,\"success\":false}")]
    [InlineData("{\"loginId\":123,\"success\":true}")]
    public async Task StaleOrMalformedCompletionCannotCompleteOrFailCurrentLogin(string notification)
    {
        using var fixture = new Fixture();
        var stale = "{\"method\":\"account/login/completed\",\"params\":" + notification + "}";
        using var output = new StringReader(string.Join('\n', Initialized, Started, Updated, stale, Completed, Account, Limits));
        using var input = new StringWriter();
        var completed = await fixture.Service.RunSessionAsync(output, input, fixture.LoginHome, 1, false, openUrl: (_, _) => { });
        Assert.True(completed);
        Assert.Equal("account-1", fixture.Auth.ReadCredentials(1).AccountId);
        var requests = input.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(requests, request => request.Contains("account/read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingCompletionIdAndUnsolicitedValidationRepliesCannotImportCredentials()
    {
        using var fixture = new Fixture();
        using var output = new StringReader(string.Join('\n', Initialized, Started, Updated,
            """{"method":"account/login/completed","params":{"success":true}}""", Account, Limits));
        using var input = new StringWriter();
        Assert.False(await fixture.Service.RunSessionAsync(output, input, fixture.LoginHome, 1, false, openUrl: (_, _) => { }));
        Assert.False(fixture.Auth.ProfileArtifactExists(1));
        Assert.DoesNotContain("account/read", input.ToString());
    }

    [Fact]
    public async Task MatchingFailureTerminatesAndDoesNotEchoServerError()
    {
        using var fixture = new Fixture();
        using var output = new StringReader(string.Join('\n', Initialized, Started,
            """{"method":"account/login/completed","params":{"loginId":"current","success":false,"error":"secret-test-value"}}"""));
        using var input = new StringWriter();
        var error = await Assert.ThrowsAsync<CodexSyncBarException>(() =>
            fixture.Service.RunSessionAsync(output, input, fixture.LoginHome, 1, false, openUrl: (_, _) => { }));
        Assert.DoesNotContain("secret-test-value", error.Message);
        Assert.False(fixture.Auth.ProfileArtifactExists(1));
    }

    [Fact]
    public async Task CancellationArrivingWithFinalReplyNeverImportsAuth()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelingReader([Initialized, Started, Updated, Completed, Account, Limits], cancellation);
        using var input = new StringWriter();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.RunSessionAsync(output, input, fixture.LoginHome, 1, false, cancellationToken: cancellation.Token, openUrl: (_, _) => { }));
        Assert.False(fixture.Auth.ProfileArtifactExists(1));
    }

    [Fact]
    public async Task DuplicateAccountLoginPreservesExistingSlotAndDoesNotCreateNewSlot()
    {
        using var fixture = new Fixture();
        fixture.Auth.ImportAuth(Path.Combine(fixture.LoginHome, "auth.json"), 1);
        using var output = new StringReader(string.Join('\n', Initialized, Started, Updated, Completed, Account, Limits));
        using var input = new StringWriter();
        await Assert.ThrowsAsync<CodexSyncBarException>(() =>
            fixture.Service.RunSessionAsync(output, input, fixture.LoginHome, 2, false, openUrl: (_, _) => { }));
        Assert.Equal("account-1", fixture.Auth.ReadCredentials(1).AccountId);
        Assert.False(fixture.Auth.ProfileArtifactExists(2));
    }

    private sealed class CancelingReader(string[] messages, CancellationTokenSource cancellation) : TextReader
    {
        private int _index;
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var message = _index < messages.Length ? messages[_index++] : null;
            if (_index == messages.Length) cancellation.Cancel();
            return ValueTask.FromResult(message);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-login-protocol-" + Guid.NewGuid().ToString("N"));
        public AuthStore Auth { get; }
        public CodexLoginService Service { get; }
        public string LoginHome { get; }
        public Fixture()
        {
            var paths = new WindowsPaths(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
            Auth = new(paths);
            Service = new(paths, Auth, new BrowserLoginService(paths));
            LoginHome = Path.Combine(paths.LoginSessionsDirectory, "synthetic-login");
            Directory.CreateDirectory(LoginHome);
            var claims = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"email\":\"test@example.com\"}"));
            WindowsPathSafety.WritePrivateBytes(Path.Combine(LoginHome, "auth.json"), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new CodexAuthFile
            {
                AuthMode = "chatgpt", Tokens = new() { AccountId = "account-1", AccessToken = "test-access", RefreshToken = "test-refresh", IdToken = "id." + claims + ".signature" },
            })));
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
}
