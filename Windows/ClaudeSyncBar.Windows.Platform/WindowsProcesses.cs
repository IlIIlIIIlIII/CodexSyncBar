using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeSyncBar.Windows.Core;

/// <summary>
/// One process of this user that matters to account switching: a Claude Code
/// CLI, Claude Desktop or a browser. The handle is held from the moment it is
/// captured, so a reused process id is never signalled.
/// </summary>
internal sealed class WindowsProcess : IDisposable
{
    public int Id { get; }
    public int ParentId { get; }
    public string Executable { get; }
    public string CommandLine { get; }
    public IReadOnlyList<string> Arguments { get; }
    public string? CurrentDirectory { get; }
    public IReadOnlyDictionary<string, string> Environment { get; }
    public bool EnvironmentReadable { get; }
    private readonly Process _process;

    private WindowsProcess(Process process, int parentId, string executable, string commandLine, string? currentDirectory,
        Dictionary<string, string> environment, bool environmentReadable)
    {
        _process = process;
        Id = process.Id;
        ParentId = parentId;
        Executable = executable;
        CommandLine = commandLine;
        Arguments = SplitCommandLine(commandLine);
        CurrentDirectory = currentDirectory;
        Environment = environment;
        EnvironmentReadable = environmentReadable;
    }

    private static readonly string[] InterestingNames = ["claude", "node", "chrome", "msedge"];

    /// <summary>The processes of this user whose image name could be one of the programs above.</summary>
    public static IEnumerable<WindowsProcess> Enumerate()
    {
        var self = System.Environment.ProcessId;
        foreach (var process in Process.GetProcesses())
        {
            WindowsProcess? value = null;
            try
            {
                if (process.Id != self && InterestingNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                    value = Capture(process);
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
            if (value is null) process.Dispose();
            else yield return value;
        }
    }

    internal static WindowsProcess? Capture(int id)
    {
        Process process;
        try { process = Process.GetProcessById(id); }
        catch (ArgumentException) { return null; }
        try
        {
            var value = Capture(process);
            if (value is null) process.Dispose();
            return value;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static readonly int CurrentSession = Process.GetCurrentProcess().SessionId;

    private static WindowsProcess? Capture(Process process)
    {
        try
        {
            if (process.SessionId != CurrentSession) return null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { return null; }
        var handle = OpenProcess(QueryLimitedInformation | VmRead, false, process.Id);
        var readable = handle != IntPtr.Zero;
        if (!readable) handle = OpenProcess(QueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero) return null;
        try
        {
            // Keeps the process object, and so its id, ours while it is examined.
            _ = process.Handle;
            var executable = ImagePath(handle);
            if (executable is null) return null;
            var commandLine = QueryCommandLine(handle) ?? "";
            var parent = 0;
            string? directory = null;
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var environmentReadable = false;
            if (NtQueryInformationProcess(handle, 0, out var basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0)
            {
                parent = (int)basic.InheritedFromUniqueProcessId;
                if (readable && System.Environment.Is64BitProcess)
                    (directory, environmentReadable) = ReadParameters(handle, basic.PebBaseAddress, environment);
            }

            if (process.HasExited) return null;
            return new WindowsProcess(process, parent, executable, commandLine, directory, environment, environmentReadable);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    /// <summary>Asks the program to close its windows, then ends it and everything it started.</summary>
    public async Task StopAsync(CancellationToken token, TimeSpan? grace = null)
    {
        if (HasExited) return;
        try { _process.CloseMainWindow(); } catch (InvalidOperationException) { }
        var deadline = DateTime.UtcNow + (grace ?? TimeSpan.FromSeconds(5));
        while (!HasExited && DateTime.UtcNow < deadline) await Task.Delay(100, token);
        if (!HasExited)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            try { await _process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token); }
            catch (TimeoutException) { }
        }

        if (!HasExited) throw new ClaudeSyncBarException("관리 대상 프로세스의 종료를 확인하지 못했습니다.");
    }

    public void Dispose() => _process.Dispose();

    /// <summary>The Claude Code CLI: the native or Desktop-bundled claude.exe, or node running the npm package.</summary>
    public bool IsClaudeCli => IsClaudeCliCommand(Executable, Arguments);

    internal static bool IsClaudeCliCommand(string executable, IReadOnlyList<string> arguments)
    {
        var name = Path.GetFileName(executable);
        if (name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase)) return !ClaudeDesktopLifecycle.IsDesktopExecutable(executable);
        return name.Equals("node.exe", StringComparison.OrdinalIgnoreCase)
            && arguments.Skip(1).Any(argument => argument.Replace('/', '\\').Contains(@"\@anthropic-ai\claude-code\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The Claude Code configuration directory the process uses, or null when it cannot be told.</summary>
    public string? ClaudeHome
    {
        get
        {
            if (!EnvironmentReadable) return null;
            var home = Environment.GetValueOrDefault("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrEmpty(home))
            {
                var profile = Environment.GetValueOrDefault("USERPROFILE");
                if (string.IsNullOrEmpty(profile)) return null;
                home = Path.Combine(profile, ".claude");
            }

            return Path.IsPathFullyQualified(home) ? Path.GetFullPath(home) : null;
        }
    }

    public bool UsesClaudeHome(WindowsPaths paths) => string.Equals(ClaudeHome, paths.ClaudeHome, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// False for sessions that never touch the credential file: those hosted by
    /// Claude Desktop and those given a token through the environment. Null when unknown.
    /// </summary>
    public bool? ReadsCredentialFile => !EnvironmentReadable ? null
        : Environment.GetValueOrDefault("CLAUDE_CODE_ENTRYPOINT") != "claude-desktop"
            && !Environment.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN");

    public bool UsesBrowserProfile(string directory)
    {
        var values = new List<string>();
        for (var i = 0; i < Arguments.Count; i++)
        {
            if (Arguments[i].StartsWith("--user-data-dir=", StringComparison.OrdinalIgnoreCase)) values.Add(Arguments[i][16..]);
            else if (Arguments[i] == "--user-data-dir" && i + 1 < Arguments.Count) values.Add(Arguments[++i]);
        }

        return values.Count == 1 && Path.IsPathFullyQualified(values[0])
            && string.Equals(Path.GetFullPath(values[0]), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the process, however many steps removed, was started by the ancestor.</summary>
    internal static bool DescendsFrom(int id, int ancestor)
    {
        var parents = new Dictionary<int, int>();
        foreach (var entry in Snapshot()) parents[entry.Id] = entry.Parent;
        return DescendsFrom(id, ancestor, parents, CreationTime);
    }

    // Windows keeps a parent's id after the parent ends and can give that id to
    // a newer process. One created after the child cannot have started it, so the
    // line of parents ends there. A start time that cannot be read is trusted.
    internal static bool DescendsFrom(int id, int ancestor, IReadOnlyDictionary<int, int> parents, Func<int, long?> started)
    {
        var childStarted = started(id);
        for (var depth = 0; depth < 128 && id > 4; depth++)
        {
            if (id == ancestor) return true;
            if (!parents.TryGetValue(id, out var parent)) return false;
            var parentStarted = started(parent);
            if (childStarted is { } child && parentStarted is { } adopted && adopted > child) return false;
            id = parent;
            childStarted = parentStarted;
        }

        return false;
    }

    // When the process was created, in file-time ticks, or null when it cannot be opened.
    private static long? CreationTime(int id)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, id);
        if (handle == IntPtr.Zero) return null;
        try { return GetProcessTimes(handle, out var created, out _, out _, out _) ? created : null; }
        finally { CloseHandle(handle); }
    }

    // Toolhelp gives every process's parent without opening any of them.
    private static List<(int Id, int Parent)> Snapshot()
    {
        var result = new List<(int, int)>();
        var snapshot = CreateToolhelp32Snapshot(0x2, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
                result.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    internal static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        for (var index = 0; index < commandLine.Length; index++)
        {
            var character = commandLine[index];
            if (character == '\\')
            {
                var start = index;
                while (index < commandLine.Length && commandLine[index] == '\\') index++;
                var count = index - start;
                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1) current.Append('"');
                    else quoted = !quoted;
                }
                else
                {
                    current.Append('\\', count);
                    index--;
                }

                started = true;
            }
            else if (character == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (started)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(character);
                started = true;
            }
        }

        if (started) result.Add(current.ToString());
        return result;
    }

    private static string? ImagePath(IntPtr handle)
    {
        var buffer = new StringBuilder(1024);
        var size = buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
    }

    private static string? QueryCommandLine(IntPtr handle)
    {
        var length = 0;
        NtQueryInformationProcess(handle, 60, IntPtr.Zero, 0, out length);
        if (length <= 0 || length > 1024 * 1024) return null;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (NtQueryInformationProcess(handle, 60, buffer, length, out _) != 0) return null;
            var text = Marshal.PtrToStructure<UnicodeString>(buffer);
            return text.Buffer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // The 64-bit PEB layout: ProcessParameters at 0x20; in it CurrentDirectory.DosPath at 0x38,
    // Environment at 0x80 and EnvironmentSize at 0x3F0.
    private static (string? Directory, bool EnvironmentReadable) ReadParameters(IntPtr handle, IntPtr peb, Dictionary<string, string> environment)
    {
        if (!ReadPointer(handle, peb + 0x20, out var parameters) || parameters == IntPtr.Zero) return (null, false);
        string? directory = null;
        if (ReadStruct<UnicodeString>(handle, parameters + 0x38, out var current) && current.Length is > 0 and < 32768)
        {
            var text = ReadString(handle, current.Buffer, current.Length);
            if (text is not null) directory = text.TrimEnd('\\') is { Length: > 2 } trimmed ? trimmed : text;
        }

        if (!ReadPointer(handle, parameters + 0x80, out var block) || block == IntPtr.Zero
            || !ReadPointer(handle, parameters + 0x3F0, out var sizeValue)) return (directory, false);
        var size = sizeValue.ToInt64();
        if (size is <= 0 or > 2 * 1024 * 1024) return (directory, false);
        var raw = ReadString(handle, block, (int)size);
        if (raw is null) return (directory, false);
        foreach (var entry in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // Entries such as "=C:=C:\path" keep the per-drive directories; they are not variables.
            var separator = entry.IndexOf('=', 1);
            if (separator > 0) environment[entry[..separator]] = entry[(separator + 1)..];
        }

        return (directory, true);
    }

    private static bool ReadPointer(IntPtr handle, IntPtr address, out IntPtr value)
    {
        var buffer = new byte[IntPtr.Size];
        value = IntPtr.Zero;
        if (!ReadProcessMemory(handle, address, buffer, buffer.Length, out var read) || read != buffer.Length) return false;
        value = (IntPtr)BitConverter.ToInt64(buffer);
        return true;
    }

    private static bool ReadStruct<T>(IntPtr handle, IntPtr address, out T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = new byte[size];
        value = default;
        if (!ReadProcessMemory(handle, address, buffer, size, out var read) || read != size) return false;
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { value = Marshal.PtrToStructure<T>(pinned.AddrOfPinnedObject()); }
        finally { pinned.Free(); }
        return true;
    }

    private static string? ReadString(IntPtr handle, IntPtr address, int bytes)
    {
        var buffer = new byte[bytes];
        return ReadProcessMemory(handle, address, buffer, bytes, out var read) && read > 0
            ? Encoding.Unicode.GetString(buffer, 0, (int)read)
            : null;
    }

    private const uint QueryLimitedInformation = 0x1000;
    private const uint VmRead = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, out ProcessBasicInformation information, int length, out int returned);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, int size, out nint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
}
