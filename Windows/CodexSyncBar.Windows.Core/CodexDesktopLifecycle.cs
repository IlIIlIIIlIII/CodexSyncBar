using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

internal interface ICodexDesktopSession : IDisposable
{
    Task StopAsync(CancellationToken cancellationToken);
    Task StartAsync(CancellationToken cancellationToken);
}

internal static class CodexDesktopLifecycle
{
    // The Store application currently uses ChatGPT.exe; older releases use Codex.exe.
    // A matching process name alone also matches the CLI or the separate ChatGPT app.
    internal static bool IsDesktopExecutable(string path) => Regex.IsMatch(
        path.Replace('\\', '/'),
        @"^[A-Za-z]:/Program Files/WindowsApps/OpenAI\.Codex_[^/]+__2p2nqsd0c76g0/app/(?:ChatGPT|Codex)\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static ICodexDesktopSession Capture(WindowsPaths paths)
    {
        var processes = new List<Process>();
        try
        {
            // Explicit isolated homes must never close the real user's desktop app.
            if (OperatingSystem.IsWindows() && Path.GetFullPath(paths.Home).Equals(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase))
            {
                using var current = Process.GetCurrentProcess();
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, SessionId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='ChatGPT.exe' OR Name='Codex.exe'");
                using var rows = searcher.Get();
                foreach (ManagementObject row in rows)
                {
                    using (row)
                    {
                        if (Convert.ToInt32(row["SessionId"]) != current.SessionId
                            || row["ExecutablePath"] is not string path || !IsDesktopExecutable(path)) continue;
                        var arguments = CodexProcessScope.SplitCommandLine(row["CommandLine"] as string ?? "");
                        if (arguments.Count == 0 || arguments.Skip(1).Any(a => a.StartsWith("--type=", StringComparison.Ordinal))) continue;
                        Process process;
                        try { process = Process.GetProcessById(Convert.ToInt32(row["ProcessId"])); }
                        catch (ArgumentException) { continue; }
                        // Opening the handle now prevents a reused PID from becoming a termination target.
                        try
                        {
                            _ = process.Handle;
                            if (!string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                            { process.Dispose(); continue; }
                            processes.Add(process);
                        }
                        catch { process.Dispose(); throw; }
                    }
                }
            }
            return new DesktopSession(processes, paths);
        }
        catch
        {
            foreach (var process in processes) process.Dispose();
            throw;
        }
    }

    private sealed class DesktopSession(List<Process> processes, WindowsPaths paths) : ICodexDesktopSession
    {
        private readonly string[] _executables = processes.Select(p => p.MainModule!.FileName!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited) continue;
                var children = CaptureChildren(process.Id);
                try
                {
                    // WaitForExit on the root alone does not wait for its backend children.
                    // Keep handles to those children until all old auth writers have exited.
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    foreach (var child in children)
                        if (!child.HasExited) child.Kill(entireProcessTree: true);
                    await Task.WhenAll(children.Append(process).Select(p => p.WaitForExitAsync(cancellationToken)))
                        .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                }
                finally { foreach (var child in children) child.Dispose(); }
            }
        }

        private static List<Process> CaptureChildren(int parentId)
        {
            if (!OperatingSystem.IsWindows()) return [];
            var children = new List<Process>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, CreationDate FROM Win32_Process");
                using var rows = searcher.Get();
                var entries = new List<(int Id, int Parent, string Created)>();
                foreach (ManagementObject row in rows)
                    using (row)
                        if (row["CreationDate"] is string created)
                            entries.Add((Convert.ToInt32(row["ProcessId"]), Convert.ToInt32(row["ParentProcessId"]), created));
                var parents = new HashSet<int> { parentId };
                while (true)
                {
                    var next = entries.Where(e => parents.Contains(e.Parent) && !parents.Contains(e.Id)).ToArray();
                    if (next.Length == 0) break;
                    foreach (var entry in next)
                    {
                        parents.Add(entry.Id);
                        Process child;
                        try { child = Process.GetProcessById(entry.Id); }
                        catch (ArgumentException) { continue; }
                        try
                        {
                            _ = child.Handle;
                            if (child.HasExited || (child.StartTime.ToUniversalTime()
                                - ManagementDateTimeConverter.ToDateTime(entry.Created).ToUniversalTime()).Duration() > TimeSpan.FromMilliseconds(1))
                            { child.Dispose(); continue; }
                            children.Add(child);
                        }
                        catch { child.Dispose(); throw; }
                    }
                }
                return children;
            }
            catch
            {
                foreach (var child in children) child.Dispose();
                throw;
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var executable in _executables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = paths.Home,
                };
                start.Environment["CODEX_HOME"] = paths.CodexHome;
                var process = Process.Start(start)
                    ?? throw new CodexSyncBarException("Codex 앱을 다시 실행하지 못했습니다.");
                processes.Add(process);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                if (process.HasExited)
                    throw new CodexSyncBarException("Codex 앱이 재실행 직후 종료되었습니다.");
            }
        }

        public void Dispose()
        {
            foreach (var process in processes) process.Dispose();
        }
    }
}
