using CodexSyncBar.Windows.Core;

namespace CodexSyncBar_Windows;

internal interface ITrayDashboard
{
    event EventHandler? TrayStateChanged;
    TrayPopoverSnapshot CreateTrayPopoverSnapshot();
    Task RefreshFromTrayAsync();
    Task RefreshTrayPopoverAsync();
    Task RefreshUsageIfStaleAsync();
    Task SelectFromTrayAsync(int profileId);
    Task ApplyFromTrayAsync(int profileId);
}

internal sealed class MainPageTrayDashboard(MainPage page) : ITrayDashboard
{
    public event EventHandler? TrayStateChanged { add => page.TrayStateChanged += value; remove => page.TrayStateChanged -= value; }
    public TrayPopoverSnapshot CreateTrayPopoverSnapshot() => page.CreateTrayPopoverSnapshot();
    public Task RefreshFromTrayAsync() => page.RefreshFromTrayAsync();
    public Task RefreshTrayPopoverAsync() => page.RefreshTrayPopoverAsync();
    public Task RefreshUsageIfStaleAsync() => page.RefreshUsageIfStaleAsync();
    public Task SelectFromTrayAsync(int profileId) => page.SelectFromTrayAsync(profileId);
    public Task ApplyFromTrayAsync(int profileId) => page.ApplyFromTrayAsync(profileId);
}

internal sealed class DemoTrayDashboard(DemoDashboardRuntime runtime) : ITrayDashboard
{
    private int _selectedId = 1;
    public event EventHandler? TrayStateChanged
    {
        add => runtime.Changed += value;
        remove => runtime.Changed -= value;
    }
    public Task RefreshFromTrayAsync() => runtime.RefreshUsageAsync();
    public Task RefreshTrayPopoverAsync() => runtime.RefreshUsageAsync();
    public Task RefreshUsageIfStaleAsync() => runtime.RefreshUsageAsync();
    public async Task SelectFromTrayAsync(int profileId)
    {
        _selectedId = profileId;
        await runtime.RefreshUsageAsync();
    }
    public async Task ApplyFromTrayAsync(int profileId)
    {
        var snapshot = await runtime.GetSnapshotAsync();
        await runtime.SwitchAccountAsync(profileId, snapshot.ConfigurationRevision);
    }

    public TrayPopoverSnapshot CreateTrayPopoverSnapshot()
    {
        // Runtime exposes a completed in-memory task; this path cannot touch auth or devices.
        var snapshot = runtime.GetSnapshotAsync().GetAwaiter().GetResult();
        var selected = snapshot.Accounts.First(a => a.ProfileId == _selectedId);
        var usage = selected.Usage!;
        var raw = new UsageSnapshot(selected.ProfileId, selected.MaskedEmail, "Demo",
            usage.Session, usage.Weekly, null, false,
            usage.ResetCredits, usage.ResetCreditExpirations, usage.UpdatedAt);
        var accounts = snapshot.Accounts.Select(account => new TrayAccountSnapshot(account.ProfileId,
            account.DisplayName, account.MaskedEmail, account.DisplayName[..1], account.ProfileId == _selectedId,
            account.ProfileId == snapshot.ActiveProfileId, false, false,
            account.ProfileId == snapshot.ActiveProfileId ? "현재 사용 중 · 데모" : "사용 가능 · 데모",
            $"{account.Usage?.Weekly?.RemainingPercent:0}%")).ToArray();
        var devices = snapshot.Devices.Select(device => new TrayDeviceSnapshot(device.Id, device.DisplayName,
            "연결됨 · 데모", snapshot.Accounts.First(account => account.ProfileId == device.ProfileId).DisplayName, true)).ToArray();
        return new TrayPopoverSnapshot(accounts, devices, _selectedId, snapshot.ActiveProfileId,
            selected.DisplayName, selected.MaskedEmail, selected.DisplayName[..1], "Demo", "예시 계정",
            raw, Enum.GetValues<UsageDisplayItem>(), null, "초기화권 3개", "7일 후 만료", "Codex SyncBar · 데모",
            snapshot.Operation?.Message ?? "안전한 QA 모드 · 실제 계정과 장치를 변경하지 않습니다.",
            false, snapshot.IsBusy, !snapshot.IsBusy, false);
    }
}
