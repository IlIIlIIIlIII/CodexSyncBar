namespace CodexSyncBar.Windows.Core;

public sealed record CursorRemoteResult(
    string DeviceId,
    string State,
    string? Model);
