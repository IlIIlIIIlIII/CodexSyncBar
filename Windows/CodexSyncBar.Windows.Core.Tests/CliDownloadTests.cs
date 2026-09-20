using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class CliDownloadTests
{
    [Fact]
    public async Task TamperedArchiveCannotReplaceExistingCliSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), "syncbar-cli-download-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new WindowsPaths(Path.Combine(root, "home"), Path.Combine(root, "local"));
            var directory = Path.Combine(paths.Home, ".codex-syncbar", "Tools");
            Directory.CreateDirectory(directory);
            var pointer = Path.Combine(directory, "current.txt");
            await File.WriteAllTextAsync(pointer, "previous-version");
            var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
            var name = $"codex-{architecture}-pc-windows-msvc.exe.zip";
            var release = JsonSerializer.Serialize(new
            {
                tag_name = "rust-v1.2.3", prerelease = false,
                assets = new[] { new { name, digest = "sha256:" + new string('0', 64), browser_download_url = $"https://github.com/openai/codex/releases/download/rust-v1.2.3/{name}" } },
            });
            using var http = new HttpClient(new FakeDownload(release));
            var service = new WindowsCliManagementService(paths, new LocalSwitchService(new AuthStore(paths), paths), http);
            var error = await Assert.ThrowsAsync<CodexSyncBarException>(() => service.InstallManagedAsync(CancellationToken.None));
            Assert.Contains("해시", error.Message);
            Assert.Equal("previous-version", await File.ReadAllTextAsync(pointer));
            Assert.Empty(Directory.GetFiles(directory, "codex.exe", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FakeDownload(string release) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.Host == "api.github.com" ? release : "tampered download"),
            });
    }
}
