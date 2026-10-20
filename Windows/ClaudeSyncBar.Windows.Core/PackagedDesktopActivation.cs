using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClaudeSyncBar.Windows.Core;

internal static class PackagedDesktopActivation
{
    internal static string GetApplicationId(Process process)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        uint length = 0;
        var error = GetApplicationUserModelId(process.SafeHandle, ref length, null);
        if (error != 122) throw new Win32Exception(error, "Codex 앱의 패키지 실행 ID를 확인하지 못했습니다.");
        var buffer = new StringBuilder(checked((int)length));
        error = GetApplicationUserModelId(process.SafeHandle, ref length, buffer);
        if (error != 0) throw new Win32Exception(error);
        return buffer.ToString();
    }

    internal static Process Start(string applicationId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), throwOnError: true)!;
        var manager = (IApplicationActivationManager)Activator.CreateInstance(type)!;
        try
        {
            // AO_NOERRORUI: propagate activation errors to the account-switch
            // rollback instead of displaying an untracked Windows error dialog.
            Marshal.ThrowExceptionForHR(manager.ActivateApplication(applicationId, null, 2, out var processId));
            var process = Process.GetProcessById(checked((int)processId));
            try { _ = process.Handle; return process; }
            catch { process.Dispose(); throw; }
        }
        finally { Marshal.FinalReleaseComObject(manager); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder? applicationId);

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string applicationId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
        [PreserveSig]
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string applicationId, IntPtr items,
            [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig]
        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string applicationId, IntPtr items, out uint processId);
    }
}
