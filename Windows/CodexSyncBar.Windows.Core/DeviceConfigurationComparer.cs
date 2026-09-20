namespace CodexSyncBar.Windows.Core;

public static class DeviceConfigurationComparer
{
    public static SshDeviceConfiguration Clone(SshDeviceConfiguration device) => new()
    {
        Id = device.Id,
        CredentialId = device.CredentialId,
        DisplayName = device.DisplayName,
        Host = device.Host,
        Port = device.Port,
        Username = device.Username,
        Authentication = device.Authentication,
        IdentityFile = device.IdentityFile,
        CertificateFile = device.CertificateFile,
        HasPassword = device.HasPassword,
        HasKeyPassphrase = device.HasKeyPassphrase,
        Enabled = device.Enabled,
    };

    public static bool AreEqual(SshDeviceConfiguration left, SshDeviceConfiguration right) =>
        string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase)
        && left.CredentialId == right.CredentialId
        && string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal)
        && string.Equals(left.Host, right.Host, StringComparison.Ordinal)
        && left.Port == right.Port
        && string.Equals(left.Username, right.Username, StringComparison.Ordinal)
        && string.Equals(left.Authentication, right.Authentication, StringComparison.Ordinal)
        && string.Equals(left.IdentityFile, right.IdentityFile, StringComparison.Ordinal)
        && string.Equals(left.CertificateFile, right.CertificateFile, StringComparison.Ordinal)
        && left.HasPassword == right.HasPassword
        && left.HasKeyPassphrase == right.HasKeyPassphrase
        && left.Enabled == right.Enabled;

    public static bool HasSameCredentialEndpoint(
        SshDeviceConfiguration left,
        SshDeviceConfiguration right) =>
        string.Equals(left.Host, right.Host, StringComparison.Ordinal)
        && left.Port == right.Port
        && string.Equals(left.Username, right.Username, StringComparison.Ordinal)
        && string.Equals(left.Authentication, right.Authentication, StringComparison.Ordinal)
        && string.Equals(left.IdentityFile, right.IdentityFile, StringComparison.Ordinal)
        && string.Equals(left.CertificateFile, right.CertificateFile, StringComparison.Ordinal);
}
