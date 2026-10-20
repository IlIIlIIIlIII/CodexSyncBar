using System.Diagnostics;
using System.Reflection;
using System.Text;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Windows.Tests;

/// <summary>
/// Runs the askpass exe the app ships the way ssh.exe does: with the environment
/// SshDeviceService gives ssh.exe and the prompt as the only argument, against the
/// secret store of an isolated home.
/// </summary>
public sealed class AskPassTests : IDisposable
{
    private const string PasswordPrompt = "me@build-box's password: ";
    private const string PassphrasePrompt = @"Enter passphrase for key 'C:\Users\me/.ssh/id_ed25519': ";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "csb-askpass-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsPaths _paths;
    private readonly WindowsSecretStore _store;

    public AskPassTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new WindowsPaths(Path.Combine(_root, "home"), runtimeDirectory: Path.Combine(AppContext.BaseDirectory, "Runtime"));
        Directory.CreateDirectory(_paths.Home);
        _store = new WindowsSecretStore(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void HandsSshTheStoredPasswordAndNothingElse()
    {
        const string password = "pässwörd 비밀 \"q\" ' %PATH%";
        var device = Device("password");
        _store.Save(password, $"{device.CredentialId:D}.password");

        var environment = SshEnvironment(device);
        Assert.Equal(_paths.BundledWindowsAskPass, environment["SSH_ASKPASS"]);
        Assert.Equal("force", environment["SSH_ASKPASS_REQUIRE"]);
        // Only the name of the secret travels with ssh.exe; the helper reads the value itself.
        Assert.DoesNotContain(environment.Values, value => value?.Contains("pässwörd") == true);

        Assert.Equal((0, password, ""), Run(environment, PasswordPrompt));
    }

    [Fact]
    public void GivesTheKeyPassphraseOnlyToTheKeyPrompt()
    {
        var device = Device("privateKey");
        _store.Save("key phrase", $"{device.CredentialId:D}.passphrase");
        var environment = SshEnvironment(device);

        Assert.Equal((0, "key phrase", ""), Run(environment, PassphrasePrompt));
        // A server asking for a password after the key must not receive the key's passphrase.
        Assert.Equal((1, "", ""), Run(environment, PasswordPrompt));
    }

    [Theory]
    [InlineData("SSH_ASKPASS_PROMPT", "confirm")]
    [InlineData("SSH_ASKPASS_PROMPT", "none")]
    [InlineData("CLAUDE_SYNCBAR_CREDENTIAL_ID", null)]
    [InlineData("CLAUDE_SYNCBAR_CREDENTIAL_ID", "..\\secrets\\x")]
    [InlineData("CLAUDE_SYNCBAR_CREDENTIAL_ID", "{00000000-0000-0000-0000-000000000000}")]
    [InlineData("CLAUDE_SYNCBAR_SECRET_KIND", null)]
    [InlineData("CLAUDE_SYNCBAR_SECRET_KIND", "token")]
    public void RefusesWhenTheEnvironmentIsWrong(string name, string? value)
    {
        var device = Device("password");
        _store.Save("hunter2", $"{device.CredentialId:D}.password");
        var environment = SshEnvironment(device);
        environment[name] = value;

        var (code, output, error) = Run(environment, PasswordPrompt);
        Assert.NotEqual(0, code);
        Assert.Empty(output);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData]
    [InlineData("Enter PIN for ED25519-SK key C:\\Users\\me/.ssh/id_ed25519_sk: ")]
    [InlineData("Enter new password: ")]
    [InlineData("Retype new password: ")]
    [InlineData(PasswordPrompt, "extra")]
    public void AnswersOnlyThePromptForItsKindOfSecret(params string[] prompt)
    {
        var device = Device("password");
        _store.Save("hunter2", $"{device.CredentialId:D}.password");

        var (code, output, error) = Run(SshEnvironment(device), prompt);
        Assert.NotEqual(0, code);
        Assert.Empty(output);
        Assert.Empty(error);
    }

    [Fact]
    public void RefusesADeviceWithNothingStored()
    {
        Assert.Equal((1, "", ""), Run(SshEnvironment(Device("password")), PasswordPrompt));
    }

    private static SshDeviceConfiguration Device(string authentication) => new()
    {
        Id = "build-box",
        CredentialId = Guid.NewGuid(),
        Host = "build-box",
        Username = "me",
        Authentication = authentication,
        HasPassword = authentication == "password",
        HasKeyPassphrase = authentication == "privateKey",
    };

    // Exactly what SshDeviceService adds to ssh.exe's environment, plus the isolated home
    // the helper reads instead of this user's real store.
    private Dictionary<string, string?> SshEnvironment(SshDeviceConfiguration device)
    {
        var service = new SshDeviceService(new AuthStore(_paths), _paths);
        var askPassEnvironment = typeof(SshDeviceService).GetMethod("AskPassEnvironment", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var environment = new Dictionary<string, string?>((IDictionary<string, string?>)askPassEnvironment.Invoke(service, [device, "stored"])!)
        {
            ["CLAUDE_SYNCBAR_STATE_ROOT"] = _paths.StateRoot,
            ["CLAUDE_CONFIG_DIR"] = _paths.ClaudeHome,
        };
        return environment;
    }

    private static (int Code, string Output, string Error) Run(IDictionary<string, string?> environment, params string[] arguments)
    {
        var start = new ProcessStartInfo(environment["SSH_ASKPASS"]!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("SSH_ASKPASS_PROMPT");
        foreach (var (name, value) in environment)
        {
            if (value is null) start.Environment.Remove(name);
            else start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30000));
        return (process.ExitCode, output.Result, error.Result);
    }
}
