namespace CodexSyncBar.Windows.Core;

public sealed record SecretCleanupIntent(
    Guid CredentialId,
    string Path);

public sealed record PreparedDeviceSave(
    SshDeviceConfiguration Device,
    Guid? ReplacedCredentialId,
    bool RequiresActivationValidation,
    IReadOnlyList<SecretCleanupIntent> SecretCleanupIntents);
