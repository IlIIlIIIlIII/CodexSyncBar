using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class SshAskPassTests
{
    [Fact]
    public void MaterializationCopiesOnlyPayloadAndRejectsTamperedCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "syncbar-askpass-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "packaged-helper.exe");
        try
        {
            Directory.CreateDirectory(root);
            var paths = new WindowsPaths(Path.Combine(root, "home"), Path.Combine(root, "appdata"));
            byte[] payload = [77, 90, 0, 13, 10, 255];
            File.WriteAllBytes(source, payload);
            File.SetAttributes(source, FileAttributes.ReadOnly);

            var executable = SshDeviceService.MaterializeAskPassExecutable(paths, source);

            Assert.Equal(paths.ExternalRuntimeDirectory, Path.GetDirectoryName(executable));
            Assert.Equal(payload, File.ReadAllBytes(executable));
            Assert.Equal(0, (int)(File.GetAttributes(executable) & FileAttributes.ReadOnly));
            WindowsPathSafety.EnsurePrivateFile(executable, "test helper", 1024);
            Assert.Empty(Directory.GetFiles(paths.ExternalRuntimeDirectory, ".askpass-*"));
            Assert.Equal(executable, SshDeviceService.MaterializeAskPassExecutable(paths, source));

            File.WriteAllText(executable, "tampered");
            Assert.Throws<CodexSyncBarException>(() =>
                SshDeviceService.MaterializeAskPassExecutable(paths, source));
            Assert.Equal(payload, File.ReadAllBytes(source));
        }
        finally
        {
            if (File.Exists(source)) File.SetAttributes(source, FileAttributes.Normal);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
