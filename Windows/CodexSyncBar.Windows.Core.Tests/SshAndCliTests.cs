using System.Text;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public class SshAndCliTests
{
    [Theory]
    [InlineData("REMOTE HOST IDENTIFICATION HAS CHANGED!\nHost key verification failed.")]
    [InlineData("REVOKED HOST KEY DETECTED")]
    [InlineData("Connection timed out")]
    public void ChangedRevokedOrUnreachableHostCannotEnterTrustFlow(string error) =>
        Assert.Throws<CodexSyncBarException>(() => SshHostTrust.RequiresRegistration(new(255, "", error)));

    [Fact]
    public void UnknownHostRequiresTrustAndKnownHostProceedsToAuthentication()
    {
        Assert.True(SshHostTrust.RequiresRegistration(new(255, "", "No ED25519 host key is known. Host key verification failed.")));
        Assert.False(SshHostTrust.RequiresRegistration(new(255, "", "user@host: Permission denied (publickey,password).")));
    }

    [Fact]
    public void ShellInputUsesUnixNewlinesAndAlwaysEndsWithNewline() =>
        Assert.Equal("if true; then\n  echo ok\nfi\n", SshDeviceService.NormalizeRemoteInput("if true; then\r\n  echo ok\r\nfi"));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(22.5)]
    public void InvalidPortRejected(double port) => Assert.Throws<CodexSyncBarException>(() =>
        SshDeviceInput.Validate("server", "user", port, "password", "", "secret", false));

    [Fact]
    public void ExistingPasswordCanBeRetainedButNewPasswordIsRequired()
    {
        SshDeviceInput.Validate("server", "user", 22, "password", "", "", true);
        Assert.Throws<CodexSyncBarException>(() => SshDeviceInput.Validate("server", "user", 22, "password", "", "", false));
        SshDeviceInput.Validate("server", "user", 22, "openSSHConfig", "", "", false);
    }

    [Fact]
    public void PasswordModeNeverPassesStaleIdentityFiles()
    {
        var options = SshDeviceService.BuildCommonOptions(new() { Authentication = "password", IdentityFile = "old-key", CertificateFile = "old-cert" }, false, true);
        Assert.DoesNotContain("-i", options);
        Assert.DoesNotContain(options, value => value.StartsWith("CertificateFile="));
        Assert.Contains("StrictHostKeyChecking=yes", options);
        Assert.Contains("PreferredAuthentications=password", options);
    }

    [Fact]
    public void UpdatePendingIsSuccessWithDistinctReconnectState()
    {
        var result = CliUpdateResult.Parse(new(2, "npm output\nbefore=1.0.0 after=2.0.0 manager=npm restart=reconnect-pending\n", ""));
        Assert.Equal("2.0.0", result.After);
        Assert.Contains("재연결 대기", result.DisplayText);
        Assert.Throws<CodexSyncBarException>(() => CliUpdateResult.Parse(new(1, "", "network failed")));
        Assert.Throws<CodexSyncBarException>(() => CliUpdateResult.Parse(new(0, "before=1.0.0 manager=npm", "")));
    }

    [Fact]
    public void HostFingerprintMatchesOpenSshSha256Format() =>
        Assert.Equal("SHA256:ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0", SshHostTrust.Fingerprint(Convert.ToBase64String(Encoding.UTF8.GetBytes("abc"))));
}
