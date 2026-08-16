using Microsoft.Win32;
using System.Diagnostics;

namespace CodexSyncBar_Windows;

public static class LaunchAtLoginService
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "CodexSyncBar";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null)
        {
            throw new InvalidOperationException("Windows 로그인 시작 설정을 열지 못했습니다.");
        }

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
    }

    public static void OpenSettings()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "ms-settings:startupapps",
            UseShellExecute = true,
        });
    }

    private static string BuildCommand()
    {
        try
        {
            var packageId = Windows.ApplicationModel.Package.Current.Id;
            if (!string.IsNullOrWhiteSpace(packageId.FamilyName))
            {
                return $"explorer.exe shell:AppsFolder\\{packageId.FamilyName}!App";
            }
        }
        catch
        {
            // Unpackaged WinUI launches do not have a package identity.
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var safePath = processPath.Replace("\"", string.Empty);
            return $"\"{safePath}\"";
        }

        return "explorer.exe shell:AppsFolder\\CodexSyncBar!App";
    }
}
