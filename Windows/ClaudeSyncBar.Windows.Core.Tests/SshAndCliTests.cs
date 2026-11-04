using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Windows.Core.Tests;

public class SshAndCliTests
{
    [Fact]
    public void ShellInputUsesUnixNewlinesAndAlwaysEndsWithNewline() =>
        Assert.Equal("if true; then\n  echo ok\nfi\n", SshDeviceService.NormalizeRemoteInput("if true; then\r\n  echo ok\r\nfi"));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(22.5)]
    public void InvalidPortRejected(double port) => Assert.Throws<ClaudeSyncBarException>(() =>
        SshDeviceInput.Validate("server", "user", port, "password", "", "secret", false));

    [Fact]
    public void ExistingPasswordCanBeRetainedButNewPasswordIsRequired()
    {
        SshDeviceInput.Validate("server", "user", 22, "password", "", "", true);
        Assert.Throws<ClaudeSyncBarException>(() => SshDeviceInput.Validate("server", "user", 22, "password", "", "", false));
        SshDeviceInput.Validate("server", "user", 22, "openSSHConfig", "", "", false);
    }

    [Fact]
    public void PasswordModeNeverPassesStaleIdentityFiles()
    {
        var options = SshDeviceService.BuildCommonOptions(new() { Authentication = "password", IdentityFile = "old-key", CertificateFile = "old-cert" }, false, true);
        Assert.DoesNotContain("-i", options);
        Assert.DoesNotContain(options, value => value.StartsWith("CertificateFile="));
        Assert.Contains("PreferredAuthentications=password", options);
    }

    [Fact]
    public void NewHostsAreRecordedWithoutAskingAndChangedKeysStillRefused() =>
        Assert.Contains("StrictHostKeyChecking=accept-new", SshDeviceService.BuildCommonOptions(new(), false, false));

    [Fact]
    public void UpdatePendingIsSuccessWithDistinctReconnectState()
    {
        var result = CliUpdateResult.Parse(new(2, "npm output\nbefore=1.0.0 after=2.0.0 manager=npm restart=reconnect-pending\n", ""));
        Assert.Equal("2.0.0", result.After);
        Assert.Contains("재연결 대기", result.DisplayText);
        Assert.Throws<ClaudeSyncBarException>(() => CliUpdateResult.Parse(new(1, "", "network failed")));
        Assert.Throws<ClaudeSyncBarException>(() => CliUpdateResult.Parse(new(0, "before=1.0.0 manager=npm", "")));
    }
}
