using CodexSyncBar.Windows.Core;

namespace CodexSyncBar_Windows;

internal sealed record TrayAccountSnapshot(
    int Id,
    string Alias,
    string Email,
    string ShortName,
    bool IsSelected,
    bool IsActive,
    bool NeedsLogin,
    bool IsPending,
    string StatusText,
    string UsageText);

internal sealed record TrayDeviceSnapshot(
    string Id,
    string DisplayName,
    string StateText,
    string AccountText,
    bool IsReachable);

internal sealed record TrayPopoverSnapshot(
    IReadOnlyList<TrayAccountSnapshot> Accounts,
    IReadOnlyList<TrayDeviceSnapshot> Devices,
    int? SelectedProfileId,
    int? ActiveProfileId,
    string SelectedAlias,
    string SelectedEmail,
    string SelectedShortName,
    string Plan,
    string AuthenticationText,
    UsageSnapshot? Usage,
    IReadOnlyList<UsageDisplayItem> VisibleUsageItems,
    string? UsageError,
    string CreditsText,
    string ResetCreditsText,
    string TrayTitle,
    string? Banner,
    bool BannerIsError,
    bool IsBusy,
    bool CanApply,
    bool HasDeviceMismatch);
