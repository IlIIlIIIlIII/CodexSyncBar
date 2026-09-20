using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class CodexProcessScopeTests
{
    private const string Home = @"C:\Users\Alice\.codex";

    [Theory]
    [InlineData("codex.exe", "codex.exe app-server proxy --sock C:\\Users\\Alice\\.codex\\app-server.sock")]
    [InlineData("codex.exe", "codex.exe app-server proxy --sock=c:/users/alice/.codex/socket")]
    [InlineData("codex.exe", "codex.exe app-server --listen unix:///C:/Users/Alice/.codex/socket")]
    [InlineData("codex.exe", "codex.exe app-server --listen=unix://C:/Users/Alice/.codex/socket")]
    [InlineData("node.exe", "node.exe C:\\Tools\\codex.js app-server proxy --sock C:\\Users\\Alice\\.codex\\socket")]
    public void ExplicitSocketWithinConfiguredHomeIsConfirmed(string processName, string commandLine) =>
        Assert.Equal(CodexProcessTarget.CurrentHome, CodexProcessScope.Classify(processName, commandLine, Home));

    [Theory]
    [InlineData("codex.exe app-server proxy --sock C:\\Users\\Alice\\other-home\\socket")]
    [InlineData("codex.exe app-server proxy --sock C:\\Users\\Alice\\.codex-other\\socket")]
    [InlineData("codex.exe app-server proxy --sock C:\\Users\\Alice\\.codex\\..\\other-home\\socket")]
    public void AnotherHomeOrLookalikePrefixIsNeverConfirmed(string commandLine) =>
        Assert.Equal(CodexProcessTarget.OtherHome, CodexProcessScope.Classify("codex.exe", commandLine, Home));

    [Theory]
    [InlineData("codex.exe app-server proxy")]
    [InlineData("codex.exe app-server proxy --sock relative/socket")]
    [InlineData("codex.exe app-server proxy --sock %CODEX_HOME%/socket")]
    [InlineData("codex.exe app-server proxy --sock C:\\Users\\Alice\\.codex\\socket --sock C:\\other\\socket")]
    [InlineData("codex.exe app-server --listen unix://127.0.0.1")]
    public void DefaultProxyOrAmbiguousPathRequiresReconnectionWithoutTermination(string commandLine) =>
        Assert.Equal(CodexProcessTarget.Unknown, CodexProcessScope.Classify("codex.exe", commandLine, Home));

    [Theory]
    [InlineData("codex.exe exec --model gpt-5")]
    [InlineData("codex.exe exec \"explain app-server proxy --sock C:\\Users\\Alice\\.codex\\socket\"")]
    [InlineData("codex.exe -c \"text=app-server proxy --sock C:\\Users\\Alice\\.codex\\socket\" exec")]
    [InlineData("codex.exe app-server --stdio")]
    [InlineData("codex.exe app-server proxy --sock C:\\Users\\Alice\\.codex\\socket --help")]
    public void GeneralCliAndReadOnlyHelpAreNeverTargets(string commandLine) =>
        Assert.Equal(CodexProcessTarget.NotAppServer, CodexProcessScope.Classify("codex.exe", commandLine, Home));

    [Fact]
    public void ShellWrapperCannotProveItsEntireProcessBelongsToOneAuthHome() =>
        Assert.Equal(CodexProcessTarget.Unknown, CodexProcessScope.Classify("cmd.exe",
            "cmd.exe /c codex.exe app-server proxy --sock C:\\Users\\Alice\\.codex\\socket", Home));

    [Fact]
    public void QuotedExecutableAndHomeWithSpacesUseTheActualArgumentBoundary() =>
        Assert.Equal(CodexProcessTarget.CurrentHome, CodexProcessScope.Classify("codex.exe",
            "\"C:\\Program Files\\Codex\\codex.exe\" app-server proxy --sock \"C:\\Users\\Alice Smith\\.codex\\socket\"",
            @"C:\Users\Alice Smith\.codex"));
}
