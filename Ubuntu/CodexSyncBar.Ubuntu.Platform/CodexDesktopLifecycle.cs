using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

internal interface ICodexDesktopSession : IDisposable
{
    Task StopAsync(CancellationToken cancellationToken);
    Task StartAsync(CancellationToken cancellationToken);
}

internal sealed record DesktopProcessEvidence(int Id, int ParentId, ulong StartTime, string Executable,
    string[] Arguments, IReadOnlyDictionary<string, string> Environment, bool EnvironmentReadable, bool IsAppServer)
{
    internal static DesktopProcessEvidence From(LinuxProcess process) => new(process.Id, process.ParentId,
        process.StartTime, process.Executable, process.Arguments, process.Environment, process.EnvironmentReadable, process.IsAppServer);
}

internal static class CodexDesktopLifecycle
{
    private static readonly Regex ChildType = new(@"(?:^|\s)--type(?:=|\s|$)", RegexOptions.CultureInvariant);
    internal static bool IsDesktopExecutable(string path)
    {
        var name = Path.GetFileName(path);
        return path == "/usr/lib/chatgpt/ChatGPT" || name is "codex" or "codex-desktop" or "Codex" or "ChatGPT"
            && Path.GetDirectoryName(path) is "/opt/Codex" or "/opt/codex";
    }
    internal static bool IsDesktopRoot(DesktopProcessEvidence process, IReadOnlyList<DesktopProcessEvidence> processes) =>
        IsDesktopExecutable(process.Executable) && !process.Arguments.Any(a => ChildType.IsMatch(a))
        && !processes.Any(parent => parent.Executable == process.Executable && IsDescendant(process, parent, processes));

    internal static bool IsDescendant(DesktopProcessEvidence process, DesktopProcessEvidence ancestor,
        IReadOnlyList<DesktopProcessEvidence> processes)
    {
        var seen = new HashSet<int> { process.Id };
        var current = process;
        while (current.ParentId > 1 && seen.Add(current.ParentId))
        {
            var parent = processes.FirstOrDefault(p => p.Id == current.ParentId);
            // Every captured process is same-user and pinned during the decision.
            // A reused PID cannot be an older ancestor of a process already born.
            if (parent is null || parent.StartTime > current.StartTime) return false;
            if (parent.Id == ancestor.Id) return parent.StartTime == ancestor.StartTime;
            current = parent;
        }
        return false;
    }
    private static bool IsPackagedServer(DesktopProcessEvidence process, DesktopProcessEvidence root) =>
        process.IsAppServer && process.Executable == Path.Combine(Path.GetDirectoryName(root.Executable)!, "resources", "codex");

    internal static string? ResolveCodexHome(DesktopProcessEvidence root, IReadOnlyList<DesktopProcessEvidence> processes)
    {
        var descendants = processes.Where(p => IsPackagedServer(p, root) && IsDescendant(p, root, processes)).ToArray();
        if (descendants.Any(p => LinuxProcess.GetCodexHome(p.Environment, p.EnvironmentReadable) is null)) return null;
        var homes = descendants.Select(p => LinuxProcess.GetCodexHome(p.Environment, p.EnvironmentReadable))
            .Append(LinuxProcess.GetCodexHome(root.Environment, root.EnvironmentReadable))
            .Where(h => h is not null).Distinct(StringComparer.Ordinal).ToArray();
        if (homes.Length > 1)
            throw new CodexSyncBarException("Codex 앱에 서로 다른 인증 경로의 연결이 있습니다. 앱을 닫은 뒤 다시 시도해 주세요.");
        return homes.SingleOrDefault();
    }

    internal static Dictionary<string, string> BuildRestartEnvironment(DesktopProcessEvidence root,
        IReadOnlyList<DesktopProcessEvidence> processes, WindowsPaths paths,
        IReadOnlyDictionary<string, string> sessionEnvironment)
    {
        // A child can inherit app-server-specific secrets or ELECTRON_RUN_AS_NODE.
        // Copy only desktop session/locale settings, never its full environment.
        static bool Allowed(string key) => key is "PATH" or "LANG" or "LANGUAGE" or "DISPLAY" or "WAYLAND_DISPLAY"
            or "DBUS_SESSION_BUS_ADDRESS" or "XAUTHORITY" or "DESKTOP_SESSION" or "XDG_CURRENT_DESKTOP"
            or "XDG_SESSION_DESKTOP" or "XDG_SESSION_TYPE" or "XDG_SESSION_ID" or "XDG_RUNTIME_DIR"
            or "XDG_DATA_DIRS" or "XDG_CONFIG_DIRS" or "XDG_DATA_HOME" or "XDG_CONFIG_HOME" or "XDG_CACHE_HOME"
            or "XDG_STATE_HOME" or "GDK_BACKEND" or "GDK_SCALE" or "GDK_DPI_SCALE" or "GTK_THEME"
            or "ELECTRON_OZONE_PLATFORM_HINT" or "OZONE_PLATFORM" or "QT_QPA_PLATFORM"
            || key.StartsWith("LC_", StringComparison.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Copy(IReadOnlyDictionary<string, string> source)
        { foreach (var (key, value) in source) if (Allowed(key)) result[key] = value; }
        Copy(sessionEnvironment);
        foreach (var server in processes.Where(p => IsPackagedServer(p, root) && IsDescendant(p, root, processes))) Copy(server.Environment);
        Copy(root.Environment);
        result["HOME"] = paths.Home;
        result["USER"] = System.Environment.UserName;
        result["LOGNAME"] = System.Environment.UserName;
        result["CODEX_HOME"] = paths.CodexHome;
        return result;
    }

    internal static ICodexDesktopSession Capture(WindowsPaths paths)
    {
        var observed = LinuxProcess.Enumerate().ToArray();
        var retained = new List<DesktopSource>();
        try
        {
            var evidence = observed.Select(DesktopProcessEvidence.From).ToArray();
            var sessionEnvironment = System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(e => e.Key is string && e.Value is string).ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);
            foreach (var process in observed)
            {
                var root = evidence.First(p => p.Id == process.Id);
                if (!IsDesktopRoot(root, evidence)) continue;
                var home = ResolveCodexHome(root, evidence);
                if (home is null && !paths.IsIsolated)
                    throw new CodexSyncBarException("실행 중인 Codex 앱의 인증 경로를 확인하지 못했습니다. 앱을 닫은 뒤 다시 시도해 주세요.");
                if (home != paths.CodexHome) continue;
                if (process.HasExited) throw new CodexSyncBarException("Codex 앱 상태가 바뀌었습니다. 다시 시도해 주세요.");
                retained.Add(new(process, BuildRestartEnvironment(root, evidence, paths, sessionEnvironment)));
            }
            return new DesktopSession(retained, paths);
        }
        catch
        {
            retained.Clear();
            throw;
        }
        finally
        {
            foreach (var process in observed)
                if (!retained.Any(source => ReferenceEquals(source.Process, process))) process.Dispose();
        }
    }
    private sealed record DesktopSource(LinuxProcess Process, Dictionary<string, string> Environment);
    private sealed class DesktopSession(List<DesktopSource> sources, WindowsPaths paths) : ICodexDesktopSession
    {
        private readonly List<LinuxProcess> _restarted = [];
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var source in sources) await source.Process.StopAsync(cancellationToken);
            // These pidfds were pinned when this session launched the process with
            // an explicit CODEX_HOME. Electron may since have erased its environ.
            foreach (var process in _restarted) await process.StopAsync(cancellationToken);
            foreach (var process in _restarted) process.Dispose();
            _restarted.Clear();
        }
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var source in sources.DistinctBy(p => p.Process.Executable))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = new ProcessStartInfo(source.Process.Executable) { UseShellExecute = false, WorkingDirectory = paths.Home };
                start.Environment.Clear();
                foreach (var (key, value) in source.Environment) start.Environment[key] = value;
                foreach (var argument in source.Process.Arguments.Skip(1)) start.ArgumentList.Add(argument);
                using var launched = Process.Start(start) ?? throw new CodexSyncBarException("Codex 앱을 다시 실행하지 못했습니다.");
                var pinned = LinuxProcess.Capture(launched.Id)
                    ?? throw new CodexSyncBarException("다시 시작한 Codex 앱의 프로세스를 확인하지 못했습니다.");
                _restarted.Add(pinned);
                await WaitForStartupAsync(pinned, cancellationToken);
            }
        }
        private async Task WaitForStartupAsync(LinuxProcess launched, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (launched.HasExited) throw new CodexSyncBarException("Codex 앱이 다시 시작된 직후 종료됐습니다.");
                var observed = LinuxProcess.Enumerate().ToArray();
                try
                {
                    var evidence = observed.Select(DesktopProcessEvidence.From).ToArray();
                    var root = evidence.FirstOrDefault(p => p.Id == launched.Id && p.StartTime == launched.StartTime);
                    if (root is not null && evidence.Any(p => IsPackagedServer(p, root) && IsDescendant(p, root, evidence)
                        && LinuxProcess.GetCodexHome(p.Environment, p.EnvironmentReadable) == paths.CodexHome)) return;
                }
                finally { foreach (var process in observed) process.Dispose(); }
                await Task.Delay(250, cancellationToken);
            }
            throw new CodexSyncBarException("Codex 앱의 새 계정 연결이 시작되지 않았습니다. 이전 상태를 복구합니다.");
        }
        public void Dispose() { foreach (var source in sources) source.Process.Dispose(); foreach (var process in _restarted) process.Dispose(); }
    }
}
