[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int]$ProcessId
)

$ErrorActionPreference = 'Stop'

if (-not [Environment]::Is64BitProcess) {
    throw 'Run this diagnostic from 64-bit PowerShell.'
}

# Only this diagnostic key is returned. Other environment entries are never
# decoded, logged, persisted, or exposed to the caller. The process is read only.
if (-not ('CodexOverrideEnvironmentProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class CodexOverrideEnvironmentProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort machine, out ushort nativeMachine);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address,
        byte[] buffer, IntPtr size, out IntPtr read);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int kind,
        byte[] information, int length, out int returned);

    private static byte[] Read(IntPtr process, long address, int length)
    {
        byte[] data = new byte[length];
        IntPtr read;
        if (!ReadProcessMemory(process, new IntPtr(address), data, new IntPtr(length), out read)
            || read.ToInt64() != length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Process metadata read failed.");
        return data;
    }

    public static string ReadOnlyOverride(int processId)
    {
        // PROCESS_QUERY_INFORMATION | PROCESS_VM_READ; no write or execution access.
        IntPtr process = OpenProcess(0x0410, false, processId);
        if (process == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Process metadata access failed.");
        try
        {
            ushort machine, nativeMachine;
            if (!IsWow64Process2(process, out machine, out nativeMachine))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Process architecture query failed.");
            if (!((machine == 0 && nativeMachine == 0x8664) || machine == 0x8664))
                throw new NotSupportedException("This diagnostic supports x64 target processes only.");

            byte[] basic = new byte[48];
            int returned;
            if (NtQueryInformationProcess(process, 0, basic, basic.Length, out returned) != 0
                || returned < 16)
                throw new InvalidOperationException("Process metadata is unavailable.");
            long peb = BitConverter.ToInt64(basic, 8);
            long parameters = BitConverter.ToInt64(Read(process, peb + 0x20, 8), 0);
            long environment = BitConverter.ToInt64(Read(process, parameters + 0x80, 8), 0);
            if (peb == 0 || parameters == 0 || environment == 0)
                throw new InvalidOperationException("Process environment is unavailable.");
            byte[] target = Encoding.Unicode.GetBytes("CODEX_NODE_REPL_PATH=");
            List<byte> entry = new List<byte>();
            // Bounded reads stop at the first double NUL. Unknown keys remain bytes.
            for (int offset = 0; offset < 1048576; offset += 2)
            {
                byte[] pair = Read(process, environment + offset, 2);
                if (pair[0] == 0 && pair[1] == 0)
                {
                    if (entry.Count == 0) return null;
                    bool match = entry.Count >= target.Length;
                    for (int i = 0; match && i < target.Length; i++) match = entry[i] == target[i];
                    if (match)
                        return Encoding.Unicode.GetString(entry.ToArray(), target.Length,
                            entry.Count - target.Length);
                    entry.Clear();
                }
                else
                {
                    entry.Add(pair[0]);
                    entry.Add(pair[1]);
                }
            }
            throw new InvalidOperationException("Environment terminator was not found within the diagnostic bound.");
        }
        finally { CloseHandle(process); }
    }
}
'@
}

$value = [CodexOverrideEnvironmentProbe]::ReadOnlyOverride($ProcessId)
[pscustomobject]@{
    ProcessId = $ProcessId
    Key = 'CODEX_NODE_REPL_PATH'
    Present = $null -ne $value
    Value = $value
}
