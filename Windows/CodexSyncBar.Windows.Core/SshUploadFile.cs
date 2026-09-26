using System.Text;

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
        contents = PrepareContents(paths, source, contents);

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

    internal static byte[] PrepareContents(WindowsPaths paths, string source, byte[] contents) =>
        IsBundledScript(paths, source)
            // Windows checkouts/packages can contain CRLF. Remote shebangs and
            // shell syntax require LF; recovery archives must remain byte-exact.
            ? Encoding.UTF8.GetBytes(SshDeviceService.NormalizeRemoteInput(Encoding.UTF8.GetString(contents)))
            : contents;

    private static bool IsBundledScript(WindowsPaths paths, string source) =>
        new[] { paths.BundledGptSwitch, paths.BundledAskPass, paths.BundledUsageSummary }
            .Contains(System.IO.Path.GetFullPath(source), StringComparer.OrdinalIgnoreCase);

    internal static void EnsureSource(WindowsPaths paths, string source)
    {
        var fullPath = System.IO.Path.GetFullPath(source);
        if (IsBundledScript(paths, fullPath))
            WindowsPathSafety.EnsureBundledFile(fullPath, paths.RuntimeDirectory);
        else
            WindowsPathSafety.EnsureFile(fullPath, "SSH 전송 원본");
    }
}
