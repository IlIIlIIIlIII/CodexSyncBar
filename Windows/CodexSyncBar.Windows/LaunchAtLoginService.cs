using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace CodexSyncBar_Windows;

public static class LaunchAtLoginService
{
    private const string TaskId = "CodexSyncBar.Startup";
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "CodexSyncBar";

    public static async Task<bool> IsEnabledAsync()
    {
        if (HasPackageIdentity())
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static async Task SetEnabledAsync(bool enabled)
    {
        if (HasPackageIdentity())
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                var state = await task.RequestEnableAsync();
                if (state is not (StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy))
                    throw new InvalidOperationException(state == StartupTaskState.DisabledByUser
                        ? "Windows 시작 앱 설정에서 Codex SyncBar를 다시 켜 주세요."
                        : "Windows 정책으로 자동 시작을 켤 수 없습니다.");
            }
            else task.Disable();
            // Remove an earlier unpackaged registration so upgrades cannot start twice.
            using var legacy = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            legacy?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath)
            ?? throw new InvalidOperationException("Windows 로그인 시작 설정을 열지 못했습니다.");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("앱 실행 파일의 위치를 확인하지 못했습니다.");
        if (executable.Contains('"')) throw new InvalidOperationException("앱 실행 파일의 경로가 올바르지 않습니다.");
        key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
    }

    public static void OpenSettings() => Process.Start(new ProcessStartInfo
    {
        FileName = "explorer.exe", Arguments = "ms-settings:startupapps", UseShellExecute = true,
    });

    private static bool HasPackageIdentity()
    {
        try { return !string.IsNullOrWhiteSpace(Package.Current.Id.FamilyName); }
        catch (InvalidOperationException) { return false; }
        catch (COMException) { return false; }
    }
}
