namespace CodexSyncBar.Windows.Core;

// MSIX redirects AppData for the app, but external OpenSSH does not see that
// redirection. Materialize uploads outside AppData before handing a path to scp.
internal sealed class SshUploadFile : IDisposable
{
    private const long MaximumBytes = 64 * 1024 * 1024;

    public string Path { get; }

    public SshUploadFile(WindowsPaths paths, string source)
    {
        EnsureSource(paths, source);
        if (new FileInfo(source).Length > MaximumBytes)
            throw new CodexSyncBarException("SSH 전송 파일 크기가 안전 한도를 초과했습니다.");
        var contents = File.ReadAllBytes(source);
        if (contents.LongLength > MaximumBytes)
            throw new CodexSyncBarException("SSH 전송 파일 크기가 안전 한도를 초과했습니다.");

        WindowsPathSafety.EnsurePrivateDirectory(paths.ExternalRuntimeDirectory, "SSH 전송 디렉터리");
        Path = System.IO.Path.Combine(paths.ExternalRuntimeDirectory, $".ssh-upload-{Guid.NewGuid():N}.tmp");
        try
        {
            // Recovery archives may contain credentials. Restrict access before
            // writing any bytes, and leave the durable recovery journal intact.
            WindowsPathSafety.WritePrivateBytes(Path, contents);
        }
        catch
        {
            File.Delete(Path);
            throw;
        }
    }

    public void Dispose() => File.Delete(Path);

    internal static void EnsureSource(WindowsPaths paths, string source)
    {
        var fullPath = System.IO.Path.GetFullPath(source);
        if (new[] { paths.BundledGptSwitch, paths.BundledAskPass, paths.BundledUsageSummary }
            .Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            WindowsPathSafety.EnsureBundledFile(fullPath, paths.RuntimeDirectory);
        else
            WindowsPathSafety.EnsureFile(fullPath, "SSH 전송 원본");
    }
}
