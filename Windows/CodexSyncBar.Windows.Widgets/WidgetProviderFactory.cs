using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace CodexSyncBar.Windows.Widgets;

[ComImport, ComVisible(false), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("00000001-0000-0000-C000-000000000046")]
internal interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr result);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
}

// COM class factory follows Microsoft's C# widget-provider sample. A singleton
// keeps state coherent when the host asks for additional COM object instances.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class WidgetProviderFactory : IClassFactory
{
    private WidgetProvider? provider;
    private readonly object providerGate = new();

    public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (outer != IntPtr.Zero) return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
        try
        {
            WidgetProvider instance;
            // A host initialization failure may recover after Windows starts its
            // widget service; do not permanently cache that exception in Lazy<T>.
            lock (providerGate) instance = provider ??= new WidgetProvider();
            var unknown = MarshalInspectable<IWidgetProvider>.FromManaged(instance);
            // Microsoft's widget host/sample also accepts the provider class GUID
            // as the requested interface during activation.
            if (iid == typeof(WidgetProvider).GUID)
            {
                result = unknown;
                return 0;
            }
            try { return Marshal.QueryInterface(unknown, in iid, out result); }
            finally { Marshal.Release(unknown); }
        }
        catch (Exception ex)
        {
            ProviderLog.Failure("class-factory", ex);
            return Marshal.GetHRForException(ex);
        }
    }

    public int LockServer(bool locked) => 0;
}
