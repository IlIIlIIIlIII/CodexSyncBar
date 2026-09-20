namespace CodexSyncBar.Windows.Core;

/// <summary>
/// Cross-process mutation lock backed by an OS file handle. Unlike a named
/// Mutex, the lease can be acquired and released across async continuations;
/// the handle is released automatically if the process exits.
/// </summary>
public sealed class ControllerMutationLock : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private ControllerMutationLock(FileStream stream)
    {
        _stream = stream;
    }

    public static async Task<ControllerMutationLock> AcquireAsync(
        WindowsPaths paths,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        paths.EnsureDirectories();
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? stream = null;
            try
            {
                var attributes = File.Exists(paths.ControllerLockFile)
                    ? File.GetAttributes(paths.ControllerLockFile)
                    : 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new CodexSyncBarException("컨트롤러 잠금 파일이 안전하지 않습니다.");
                }

                stream = new FileStream(
                    paths.ControllerLockFile,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    useAsync: false);
                stream.SetLength(0);
                using (var writer = new StreamWriter(stream, leaveOpen: true))
                {
                    writer.Write($"pid={Environment.ProcessId}\nstarted={DateTimeOffset.UtcNow:O}\n");
                    writer.Flush();
                }

                return new ControllerMutationLock(stream);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                stream?.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}
