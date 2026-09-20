namespace CodexSyncBar.Windows.Core.Tests;

public class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows DPAPI, ACLs or process APIs; exercised by Windows CI.";
    }
}

public sealed class InstalledCodexFactAttribute : WindowsFactAttribute
{
    public InstalledCodexFactAttribute()
    {
        if (OperatingSystem.IsWindows() && CodexSyncBar.Windows.Core.CodexCliLocator.Find() is null)
            Skip = "Requires an installed official Windows Codex CLI.";
    }
}

public sealed class ElevatedWindowsFactAttribute : WindowsFactAttribute
{
    public ElevatedWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            Skip = "Requires an elevated Windows administrator to assign the Administrators owner SID.";
    }
}
