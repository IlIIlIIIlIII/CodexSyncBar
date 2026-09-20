using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class SshUploadFileTests
{
    [Fact]
    public void PackageRootLinkIsAcceptedButRuntimeLinksAndEscapesAreRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "syncbar-package-link-" + Guid.NewGuid().ToString("N"));
        var alias = Path.Combine(root, "installed-app");
        var link = Path.Combine(root, "actual-app", "Runtime", "linked-helper");
        try
        {
            var runtime = Path.Combine(root, "actual-app", "Runtime");
            Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(runtime, "helper"), "helper");
            Directory.CreateSymbolicLink(alias, Path.Combine(root, "actual-app"));
            var installedRuntime = Path.Combine(alias, "Runtime");
            WindowsPathSafety.EnsureBundledFile(Path.Combine(installedRuntime, "helper"), installedRuntime);
            Assert.Throws<CodexSyncBarException>(() => WindowsPathSafety.EnsureFile(Path.Combine(installedRuntime, "helper"), "private state"));
            File.CreateSymbolicLink(link, Path.Combine(runtime, "helper"));
            Assert.Throws<CodexSyncBarException>(() => WindowsPathSafety.EnsureBundledFile(Path.Combine(installedRuntime, "linked-helper"), installedRuntime));
            Assert.Throws<CodexSyncBarException>(() => WindowsPathSafety.EnsureBundledFile(Path.Combine(root, "outside"), installedRuntime));
        }
        finally
        {
            if (File.Exists(link)) File.Delete(link);
            if (Directory.Exists(alias)) Directory.Delete(alias);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UploadIsPrivateOutsideAppDataAndRemovedEvenWhenTransferFails(bool fail)
    {
        var root = Path.Combine(Path.GetTempPath(), "syncbar-upload-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new WindowsPaths(Path.Combine(root, "home"), Path.Combine(root, "appdata"));
            Directory.CreateDirectory(paths.StateRoot);
            var source = Path.Combine(paths.StateRoot, "recovery.tar");
            byte[] contents = [0, 13, 10, 255, 42];
            File.WriteAllBytes(source, contents);
            string? staged = null;
            try
            {
                using var upload = new SshUploadFile(paths, source);
                staged = upload.Path;
                Assert.StartsWith(paths.ExternalRuntimeDirectory, staged);
                Assert.False(staged.StartsWith(paths.LocalAppData));
                WindowsPathSafety.EnsurePrivateFile(staged, "staged archive", 1024);
                Assert.Equal(contents, File.ReadAllBytes(staged));
                if (fail) throw new IOException("simulated transfer failure");
            }
            catch (IOException) when (fail) { }
            Assert.NotNull(staged);
            Assert.False(File.Exists(staged));
            Assert.Equal(contents, File.ReadAllBytes(source));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ConcurrentUploadsUseIndependentFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "syncbar-upload-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new WindowsPaths(Path.Combine(root, "home"), Path.Combine(root, "appdata"));
            Directory.CreateDirectory(paths.StateRoot);
            var source = Path.Combine(paths.StateRoot, "helper");
            File.WriteAllText(source, "helper");
            using var second = new SshUploadFile(paths, source);
            using (var first = new SshUploadFile(paths, source))
                Assert.NotEqual(first.Path, second.Path);
            Assert.Equal("helper", File.ReadAllText(second.Path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
