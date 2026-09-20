using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexSyncBar.Windows.Core;

public static class ProcessRunner
{
    private const string CmdLiteralPercent = "CODEX_SYNCBAR_LITERAL_PERCENT";
    private const string CmdLiteralBang = "CODEX_SYNCBAR_LITERAL_BANG";
    private const string CmdLiteralCaret = "CODEX_SYNCBAR_LITERAL_CARET";
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    internal const uint WaitObject0 = 0x00000000;
    internal const uint WaitTimeout = 0x00000102;

    public static CommandProcess StartInteractive(
        string fileName,
        IEnumerable<string> arguments,
        bool redirectStandardInput,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var argumentList = arguments.ToArray();
        return IsCommandScript(fileName)
            ? StartCommandScriptManaged(fileName, argumentList, redirectStandardInput, workingDirectory, environment)
            : StartExecutable(fileName, argumentList, redirectStandardInput, workingDirectory, environment);
    }

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? standardInput = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        using var process = StartInteractive(
            fileName,
            arguments,
            redirectStandardInput: standardInput is not null,
            environment: environment);

        if (standardInput is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.StandardInput.Write(standardInput);
            process.StandardInput.Flush();
            process.StandardInput.Close();
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCancellation.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(linkedCancellation.Token);
            var errorTask = process.StandardError.ReadToEndAsync(linkedCancellation.Token);
            await process.WaitForExitAsync(linkedCancellation.Token);
            return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Preserve the original timeout/cancellation exception.
            }

            throw;
        }
    }

    private static CommandProcess StartExecutable(
        string fileName,
        IReadOnlyList<string> arguments,
        bool redirectStandardInput,
        string? workingDirectory,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (redirectStandardInput)
        {
            startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        startInfo.StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        startInfo.StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ApplyEnvironment(startInfo, environment);
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new CodexSyncBarException($"프로세스를 시작하지 못했습니다: {fileName}");
            }
        }
        catch (CodexSyncBarException)
        {
            throw;
        }
        catch (Exception error)
        {
            process.Dispose();
            throw new CodexSyncBarException($"프로그램을 찾지 못했습니다: {fileName}", error);
        }

        return CommandProcess.FromManaged(process, redirectStandardInput);
    }

    private static CommandProcess StartCommandScriptManaged(
        string fileName,
        IReadOnlyList<string> arguments,
        bool redirectStandardInput,
        string? workingDirectory,
        IReadOnlyDictionary<string, string?>? environment)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new CodexSyncBarException("Windows command script는 Windows에서만 실행할 수 있습니다.");
        }

        // Use the managed ProcessStartInfo for the command host so packaged
        // WinUI launches receive the same environment and standard handles as
        // a normal executable. Keep the existing cmd-safe argument encoding;
        // without it, values such as %PATH% are expanded by cmd.exe.
        var command = BuildCmdCommand(fileName, arguments);
        var comSpec = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = comSpec,
            Arguments = $"/d /v:off /s /c \"{command}\"",
            UseShellExecute = false,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (redirectStandardInput)
        {
            startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        startInfo.StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        startInfo.StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        startInfo.Environment[CmdLiteralPercent] = "%";
        startInfo.Environment[CmdLiteralBang] = "!";
        startInfo.Environment[CmdLiteralCaret] = "^";
        ApplyEnvironment(startInfo, environment);

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new CodexSyncBarException($"Windows command script를 시작하지 못했습니다: {fileName}");
            }
        }
        catch (CodexSyncBarException)
        {
            throw;
        }
        catch (Exception error)
        {
            process.Dispose();
            throw new CodexSyncBarException($"Windows command script를 시작하지 못했습니다: {fileName}", error);
        }

        return CommandProcess.FromManaged(process, redirectStandardInput);
    }

    private static CommandProcess StartCommandScript(
        string fileName,
        IReadOnlyList<string> arguments,
        bool redirectStandardInput,
        string? workingDirectory,
        IReadOnlyDictionary<string, string?>? environment)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new CodexSyncBarException("Windows command script는 Windows에서만 실행할 수 있습니다.");
        }

        var command = BuildCmdCommand(fileName, arguments);
        var comSpec = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var commandLine = new StringBuilder($"{comSpec} /d /v:off /s /c \"{command}\"");
        var childEnvironment = BuildCommandEnvironment(environment);
        var environmentBlock = BuildEnvironmentBlock(childEnvironment);
        var environmentPointer = Marshal.StringToHGlobalUni(environmentBlock);

        SafeFileHandle? childInput = null;
        SafeFileHandle? parentInput = null;
        SafeFileHandle? childOutput = null;
        SafeFileHandle? parentOutput = null;
        SafeFileHandle? childError = null;
        SafeFileHandle? parentError = null;
        SafeFileHandle? processHandle = null;
        SafeFileHandle? threadHandle = null;
        try
        {
            var security = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                InheritHandle = true,
            };

            if (redirectStandardInput)
            {
                CreatePipePair(ref security, out parentInput, out childInput, parentIsRead: false);
            }
            else
            {
                childInput = CreateNulInputHandle(ref security);
            }

            CreatePipePair(ref security, out parentOutput, out childOutput, parentIsRead: true);
            CreatePipePair(ref security, out parentError, out childError, parentIsRead: true);

            var startupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Flags = StartfUseStdHandles,
                StandardInput = childInput.DangerousGetHandle(),
                StandardOutput = childOutput.DangerousGetHandle(),
                StandardError = childError.DangerousGetHandle(),
            };
            var started = CreateProcess(
                comSpec,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: true,
                CreateUnicodeEnvironment,
                environmentPointer,
                string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                ref startupInfo,
                out var processInformation);
            if (!started)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"Windows command script를 시작하지 못했습니다: {fileName}");
            }

            processHandle = new SafeFileHandle(processInformation.ProcessHandle, ownsHandle: true);
            threadHandle = new SafeFileHandle(processInformation.ThreadHandle, ownsHandle: true);

            childInput.Dispose();
            childInput = null;
            childOutput.Dispose();
            childOutput = null;
            childError.Dispose();
            childError = null;

            var result = CommandProcess.FromNative(
                processHandle,
                processInformation.ProcessId,
                parentInput,
                parentOutput,
                parentError,
                redirectStandardInput);
            processHandle = null;
            parentInput = null;
            parentOutput = null;
            parentError = null;
            return result;
        }
        catch (Win32Exception error)
        {
            throw new CodexSyncBarException(error.Message, error);
        }
        finally
        {
            Marshal.FreeHGlobal(environmentPointer);
            childInput?.Dispose();
            parentInput?.Dispose();
            childOutput?.Dispose();
            parentOutput?.Dispose();
            childError?.Dispose();
            parentError?.Dispose();
            processHandle?.Dispose();
            threadHandle?.Dispose();
        }
    }

    private static void CreatePipePair(
        ref SecurityAttributes security,
        out SafeFileHandle parentHandle,
        out SafeFileHandle childHandle,
        bool parentIsRead)
    {
        if (!CreatePipe(out var readHandle, out var writeHandle, ref security, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 표준 입출력 파이프를 만들지 못했습니다.");
        }

        SafeFileHandle? read = new SafeFileHandle(readHandle, ownsHandle: true);
        SafeFileHandle? write = new SafeFileHandle(writeHandle, ownsHandle: true);
        try
        {
            var parentRaw = parentIsRead ? readHandle : writeHandle;
            if (!SetHandleInformation(parentRaw, HandleFlagInherit, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 표준 입출력 핸들을 보호하지 못했습니다.");
            }

            if (parentIsRead)
            {
                parentHandle = read;
                childHandle = write;
            }
            else
            {
                parentHandle = write;
                childHandle = read;
            }

            read = null;
            write = null;
        }
        finally
        {
            read?.Dispose();
            write?.Dispose();
        }
    }

    private static SafeFileHandle CreateNulInputHandle(ref SecurityAttributes security)
    {
        var handle = CreateFile(
            "NUL",
            GenericRead,
            FileShareRead | FileShareWrite,
            ref security,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 표준 입력 핸들을 만들지 못했습니다.");
        }

        return handle;
    }

    private static Dictionary<string, string> BuildCommandEnvironment(
        IReadOnlyDictionary<string, string?>? environment)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                values[key] = value;
            }
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                {
                    values.Remove(key);
                }
                else
                {
                    values[key] = value;
                }
            }
        }

        values[CmdLiteralPercent] = "%";
        values[CmdLiteralBang] = "!";
        values[CmdLiteralCaret] = "^";
        return values;
    }

    private static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in environment.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(key).Append('=').Append(value).Append('\0');
        }

        builder.Append('\0');
        return builder.ToString();
    }

    private static void ApplyEnvironment(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string?>? environment)
    {
        if (environment is null)
        {
            return;
        }

        foreach (var (key, value) in environment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(key);
            }
            else
            {
                startInfo.Environment[key] = value;
            }
        }
    }

    private static string BuildCmdCommand(string fileName, IReadOnlyList<string> arguments)
    {
        var parts = new List<string> { QuoteCmdArgument(fileName) };
        parts.AddRange(arguments.Select(QuoteCmdArgument));
        return string.Join(' ', parts);
    }

    private static string QuoteCmdArgument(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var builder = new StringBuilder(value.Length + 32);
        builder.Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character is '\r' or '\n' or '\0')
            {
                throw new CodexSyncBarException(
                    "Windows command script 인자에 줄바꿈 또는 NUL 문자를 사용할 수 없습니다.");
            }

            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2);
                builder.Append("\"\"");
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            if (character == '^')
            {
                builder.Append('%').Append(CmdLiteralCaret).Append('%');
            }
            else if (character == '%')
            {
                builder.Append('%').Append(CmdLiteralPercent).Append('%');
            }
            else if (character == '!')
            {
                builder.Append('%').Append(CmdLiteralBang).Append('%');
            }
            else
            {
                builder.Append(character);
            }
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static bool IsCommandScript(string fileName) =>
        Path.GetExtension(fileName).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(fileName).Equals(".bat", StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out IntPtr readPipe,
        out IntPtr writePipe,
        ref SecurityAttributes pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(
        IntPtr handle,
        uint mask,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool WriteFile(
        IntPtr file,
        byte[] buffer,
        uint bytesToWrite,
        out uint bytesWritten,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(IntPtr process, uint exitCode);
}

public sealed class CommandProcess : IDisposable
{
    private readonly Process? _managedProcess;
    private readonly SafeFileHandle? _nativeProcessHandle;
    private readonly uint _nativeProcessId;
    private bool _disposed;

    private CommandProcess(Process managedProcess, bool redirectStandardInput)
    {
        _managedProcess = managedProcess;
        StandardOutput = managedProcess.StandardOutput;
        StandardError = managedProcess.StandardError;
        StandardInput = redirectStandardInput
            ? managedProcess.StandardInput
            : new StreamWriter(Stream.Null);
    }

    private CommandProcess(
        SafeFileHandle nativeProcessHandle,
        uint nativeProcessId,
        SafeFileHandle? inputHandle,
        SafeFileHandle outputHandle,
        SafeFileHandle errorHandle,
        bool redirectStandardInput)
    {
        _nativeProcessHandle = nativeProcessHandle;
        _nativeProcessId = nativeProcessId;

        StandardInput = redirectStandardInput && inputHandle is not null
            ? new StreamWriter(new PipeWriteStream(inputHandle))
            {
                AutoFlush = false,
            }
            : new StreamWriter(Stream.Null);
        StandardOutput = new StreamReader(
            new FileStream(outputHandle, FileAccess.Read, 4096, isAsync: false),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true);
        StandardError = new StreamReader(
            new FileStream(errorHandle, FileAccess.Read, 4096, isAsync: false),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true);
    }

    public StreamWriter StandardInput { get; }

    public StreamReader StandardOutput { get; }

    public StreamReader StandardError { get; }

    public bool HasExited
    {
        get
        {
            if (_managedProcess is not null)
            {
                return _managedProcess.HasExited;
            }

            return ProcessRunner.WaitForSingleObject(_nativeProcessHandle!.DangerousGetHandle(), 0) == 0;
        }
    }

    public int ExitCode
    {
        get
        {
            if (_managedProcess is not null)
            {
                return _managedProcess.ExitCode;
            }

            if (!ProcessRunner.GetExitCodeProcess(_nativeProcessHandle!.DangerousGetHandle(), out var exitCode))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "프로세스 종료 코드를 읽지 못했습니다.");
            }

            return unchecked((int)exitCode);
        }
    }

    internal static CommandProcess FromManaged(Process process, bool redirectStandardInput) =>
        new(process, redirectStandardInput);

    internal static CommandProcess FromNative(
        SafeFileHandle processHandle,
        uint processId,
        SafeFileHandle? inputHandle,
        SafeFileHandle outputHandle,
        SafeFileHandle errorHandle,
        bool redirectStandardInput) =>
        new(processHandle, processId, inputHandle, outputHandle, errorHandle, redirectStandardInput);

    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (_managedProcess is not null)
        {
            await _managedProcess.WaitForExitAsync(cancellationToken);
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ProcessRunner.WaitForSingleObject(_nativeProcessHandle!.DangerousGetHandle(), 100);
            if (result == ProcessRunner.WaitObject0)
            {
                return;
            }

            if (result != ProcessRunner.WaitTimeout)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "프로세스 종료를 기다리지 못했습니다.");
            }

            await Task.Yield();
        }
    }

    public void Kill(bool entireProcessTree = false)
    {
        if (HasExited)
        {
            return;
        }

        if (_managedProcess is not null)
        {
            _managedProcess.Kill(entireProcessTree);
            return;
        }

        if (entireProcessTree)
        {
            try
            {
                var taskKillStartInfo = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                taskKillStartInfo.ArgumentList.Add("/PID");
                taskKillStartInfo.ArgumentList.Add(_nativeProcessId.ToString());
                taskKillStartInfo.ArgumentList.Add("/T");
                taskKillStartInfo.ArgumentList.Add("/F");
                using var taskKill = Process.Start(taskKillStartInfo);
                taskKill?.WaitForExit(3000);
                if (HasExited)
                {
                    return;
                }
            }
            catch
            {
                // Fall through to terminating the root process.
            }
        }

        if (!ProcessRunner.TerminateProcess(_nativeProcessHandle!.DangerousGetHandle(), 1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "프로세스를 종료하지 못했습니다.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            StandardInput.Dispose();
        }
        catch
        {
            // A closed anonymous pipe can report an access error on a second
            // flush during disposal. The handle was already closed by the
            // caller in that case.
        }

        try
        {
            StandardOutput.Dispose();
        }
        catch
        {
        }

        try
        {
            StandardError.Dispose();
        }
        catch
        {
        }
        _nativeProcessHandle?.Dispose();
        _managedProcess?.Dispose();
    }

    private sealed class PipeWriteStream : Stream
    {
        private readonly SafeFileHandle _handle;

        public PipeWriteStream(SafeFileHandle handle)
        {
            _handle = handle;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            // Anonymous pipe handles do not support FlushFileBuffers. WriteFile
            // has already handed the bytes to the pipe.
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            cancellationToken.IsCancellationRequested
                ? Task.FromCanceled(cancellationToken)
                : Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteBuffer(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            cancellationToken.IsCancellationRequested
                ? Task.FromCanceled(cancellationToken)
                : WriteSynchronouslyAsync(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            cancellationToken.IsCancellationRequested
                ? ValueTask.FromCanceled(cancellationToken)
                : WriteSynchronouslyAsync(buffer);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _handle.Dispose();
            }

            base.Dispose(disposing);
        }

        private Task WriteSynchronouslyAsync(byte[] buffer, int offset, int count)
        {
            WriteBuffer(buffer, offset, count);
            return Task.CompletedTask;
        }

        private ValueTask WriteSynchronouslyAsync(ReadOnlyMemory<byte> buffer)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private void WriteBuffer(byte[] buffer, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
            {
                throw new ArgumentOutOfRangeException();
            }

            if (count == 0)
            {
                return;
            }

            var remaining = count;
            var currentOffset = offset;
            while (remaining > 0)
            {
                var chunk = buffer;
                if (currentOffset != 0 || remaining != buffer.Length)
                {
                    chunk = buffer[currentOffset..(currentOffset + remaining)];
                }

                if (!ProcessRunner.WriteFile(
                        _handle.DangerousGetHandle(),
                        chunk,
                        checked((uint)chunk.Length),
                        out var bytesWritten,
                        IntPtr.Zero)
                    || bytesWritten == 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(error, $"Windows 프로세스 입력을 전달하지 못했습니다 (Win32 {error}).");
                }

                currentOffset += checked((int)bytesWritten);
                remaining -= checked((int)bytesWritten);
            }
        }
    }
}
