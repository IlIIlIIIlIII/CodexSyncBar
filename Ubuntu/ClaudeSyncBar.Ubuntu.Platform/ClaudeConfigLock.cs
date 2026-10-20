using System.Runtime.InteropServices;

namespace ClaudeSyncBar.Windows.Core;

/// <summary>
/// Holds the lock directory Claude Code itself takes while it rewrites its
/// global config, so a switch never interleaves with a running session's save.
/// </summary>
internal sealed class ClaudeConfigLock : IDisposable
{
    // Claude Code refreshes a held lock well inside this window and treats an older one as abandoned.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);
    private const int AlreadyExists = 17;

    [DllImport("libc", SetLastError = true)] private static extern int mkdir(string path, uint mode);

    private readonly string _path;
    private ClaudeConfigLock(string path) => _path = path;

    internal static ClaudeConfigLock Acquire(string configFile)
    {
        var path = configFile + ".lock";
        var deadline = DateTime.UtcNow + WaitLimit;
        while (true)
        {
            if (mkdir(path, 0x1C0) == 0) return new(path);
            if (Marshal.GetLastPInvokeError() != AlreadyExists)
                throw new ClaudeSyncBarException("Claude 설정 파일의 잠금을 만들지 못했습니다.");
            if (DateTime.UtcNow >= deadline)
                throw new ClaudeSyncBarException("실행 중인 Claude Code가 설정 파일을 사용하고 있습니다. 잠시 후 다시 시도해 주세요.");
            try
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(path) > StaleAfter) Directory.Delete(path);
                else Thread.Sleep(50);
            }
            catch (IOException) { Thread.Sleep(50); }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_path); }
        catch (IOException) { /* Already taken over as stale; the new holder owns it. */ }
    }
}
