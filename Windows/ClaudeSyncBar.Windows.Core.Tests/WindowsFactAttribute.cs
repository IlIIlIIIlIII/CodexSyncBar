namespace ClaudeSyncBar.Windows.Core.Tests;

public class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows DPAPI, ACLs or process APIs; exercised by Windows CI.";
    }
}

public sealed class InstalledClaudeFactAttribute : WindowsFactAttribute
{
    public InstalledClaudeFactAttribute()
    {
        if (OperatingSystem.IsWindows() && ClaudeSyncBar.Windows.Core.ClaudeCliLocator.Find() is null)
            Skip = "Requires an installed official Windows Claude Code CLI.";
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
