using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeSyncBar.Windows.Core;
using Contracts = ClaudeSyncBar.Windows.Core.Tests.TestAuth;

namespace ClaudeSyncBar.Ubuntu.Tests;

public sealed class RecoveryEncryptionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-recovery-encryption-" + Guid.NewGuid().ToString("N"));
    private WindowsPaths Paths => new(Path.Combine(_root, "home"), Path.Combine(_root, "data"));

    [Fact]
    public void RemoteArchiveIsEncryptedAndBoundToItsOperationWithTamperDetection()
    {
        var store = new RemoteBootstrapTransactionStore(Paths);
        Paths.EnsureDirectories();
        var archive = Archive("synthetic-access-only-auth");
        var transaction = store.Begin("fixture", new string('a', 64), archive);
        var path = store.ArchivePath(transaction);
        var stored = File.ReadAllBytes(path);
        Assert.True(stored.AsSpan().StartsWith("CSBL1"u8));
        Assert.DoesNotContain("synthetic-access-only-auth", Encoding.UTF8.GetString(stored));
        Assert.Equal(archive, store.ReadArchive(transaction));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        var other = store.Begin("other-fixture", new string('b', 64), archive);
        WriteFixtureBytes(store.ArchivePath(other), stored);
        Assert.ThrowsAny<CryptographicException>(() => store.ReadArchive(other));
        var damaged = stored.ToArray();
        damaged[^1] ^= 1;
        WriteFixtureBytes(path, damaged);
        Assert.ThrowsAny<CryptographicException>(() => store.ReadArchive(transaction));
        store.Delete(other);
        store.Delete(transaction);
        Assert.Empty(store.LoadAll());
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(store.ManifestPath(transaction)));
    }

    [Fact]
    public void LoadingPrivateLegacyTarMigratesItWithoutChangingRecoverableContents()
    {
        var store = new RemoteBootstrapTransactionStore(Paths);
        Paths.EnsureDirectories();
        var archive = Archive("legacy-synthetic-auth");
        var transaction = store.Begin("fixture", new string('a', 64), archive);
        var path = store.ArchivePath(transaction);
        // Simulate the previous version's already-private tar and manifest.
        WriteFixtureBytes(path, archive);
        var loaded = Assert.Single(store.LoadAll());
        var stored = File.ReadAllBytes(path);
        Assert.True(stored.AsSpan().StartsWith("CSBL1"u8));
        Assert.Equal(archive, store.ReadArchive(loaded));
        Assert.DoesNotContain("legacy-synthetic-auth", Encoding.UTF8.GetString(stored));
        Assert.Empty(Directory.EnumerateFiles(store.DirectoryPath, "*.tmp"));
        stored[0] ^= 1;
        WriteFixtureBytes(path, stored);
        Assert.Throws<ClaudeSyncBarException>(() => store.LoadAll());
    }

    [Fact]
    public void RetainedRuntimeAuthIsEncryptedReadableAndIdempotent()
    {
        Paths.EnsureDirectories();
        var auth = new AuthStore(Paths);
        var runtime = Runtime("profile-1-");
        var path = Path.Combine(runtime, "auth.json");
        Write(path, Credentials("retained"));
        auth.ProtectRecoveryAuthFile(path);
        var encrypted = File.ReadAllBytes(path);
        Assert.Contains("protectedAuth", Encoding.UTF8.GetString(encrypted));
        Assert.DoesNotContain("refresh-retained", Encoding.UTF8.GetString(encrypted));
        Assert.Equal("refresh-retained", auth.ReadAuthFile(path).RefreshToken);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        auth.ProtectRecoveryAuthFile(path);
        Assert.Equal(encrypted, File.ReadAllBytes(path));
    }

    [Fact]
    public void TokensLeftInTheCliCredentialFileReplaceTheEarlierCopyAndLeaveNoPlaintext()
    {
        Paths.EnsureDirectories();
        var auth = new AuthStore(Paths);
        var runtime = Runtime("refresh-profile-1-");
        var path = Path.Combine(runtime, "auth.json");
        Write(path, Credentials("before"));
        var cliFile = Path.Combine(runtime, ".credentials.json");
        WindowsPathSafety.WritePrivateBytes(cliFile,
            "{\"claudeAiOauth\":{\"accessToken\":\"access-rotated\",\"refreshToken\":\"refresh-rotated\"}}"u8.ToArray());
        auth.ProtectRecoveryAuthFile(path);
        Assert.False(File.Exists(cliFile));
        Assert.DoesNotContain("refresh-rotated", File.ReadAllText(path));
        var retained = auth.ReadAuthFile(path);
        Assert.Equal("refresh-rotated", retained.RefreshToken);
        Assert.Equal("recovery-fixture-account", retained.AccountId);

        // A half-written credential file cannot be parsed, but its bytes are still kept encrypted.
        var partial = "{\"claudeAiOauth\":{\"refreshToken\":\"refresh-partial"u8.ToArray();
        WindowsPathSafety.WritePrivateBytes(cliFile, partial);
        auth.ProtectRecoveryAuthFile(path);
        Assert.DoesNotContain("refresh-partial", File.ReadAllText(cliFile));
        using var document = JsonDocument.Parse(File.ReadAllBytes(cliFile));
        Assert.Equal(partial, WindowsSecretStore.Unprotect(
            Convert.FromBase64String(document.RootElement.GetProperty("protectedAuth").GetString()!), "claude-account-auth-v1"));
        Assert.Equal("refresh-rotated", auth.ReadAuthFile(path).RefreshToken);
    }

    [Fact]
    public void IncompleteRetainedAuthBytesArePreservedInsideEncryptedEnvelope()
    {
        Paths.EnsureDirectories();
        var auth = new AuthStore(Paths);
        var path = Path.Combine(Runtime("profile-1-"), "auth.json");
        var incomplete = "{\"claudeAiOauth\":{\"refreshToken\":\"partial-synthetic-secret"u8.ToArray();
        WindowsPathSafety.WritePrivateBytes(path, incomplete);
        auth.ProtectRecoveryAuthFile(path);
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var encrypted = Convert.FromBase64String(document.RootElement.GetProperty("protectedAuth").GetString()!);
        Assert.Equal(incomplete, WindowsSecretStore.Unprotect(encrypted, "claude-account-auth-v1"));
        Assert.DoesNotContain("partial-synthetic-secret", File.ReadAllText(path));
    }

    [Fact]
    public void PendingRefreshWithNewerCanonicalGenerationRetainsOnlyEncryptedCandidate()
    {
        Paths.EnsureDirectories();
        var auth = new AuthStore(Paths);
        var source = Path.Combine(Paths.LoginSessionsDirectory, "source.json");
        Write(source, Credentials("original"));
        auth.ImportAuth(source, 1);
        auth.SwitchActive(1);
        var runtime = Runtime("refresh-profile-1-");
        var store = new AuthRefreshTransactionStore(Paths, auth);
        store.Begin(runtime, 1, auth.ReadActiveAuth()!);
        var retained = Path.Combine(runtime, "auth.json");
        Write(retained, Credentials("candidate"));
        Write(source, Credentials("newer-canonical"));
        auth.ImportAuth(source, 1, replaceExisting: true);

        Assert.Equal(Path.GetFileName(runtime), Assert.Single(store.Recover()));
        Assert.Contains("protectedAuth", File.ReadAllText(retained));
        Assert.DoesNotContain("refresh-candidate", File.ReadAllText(retained));
        Assert.Equal("refresh-candidate", auth.ReadAuthFile(retained).RefreshToken);
        Assert.Equal("refresh-newer-canonical", auth.ReadCredentials(1).RefreshToken);
        Assert.Equal("refresh-original", auth.ReadActiveAuth()!.RefreshToken);
    }

    private string Runtime(string prefix)
    {
        var directory = Path.Combine(Paths.LoginSessionsDirectory, prefix + Guid.NewGuid().ToString("N"));
        WindowsPathSafety.EnsurePrivateDirectory(directory, "isolated recovery fixture");
        return directory;
    }

    private static byte[] Archive(string contents)
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Ustar, leaveOpen: true))
        {
            using var data = new MemoryStream(Encoding.UTF8.GetBytes(contents));
            writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, ".claude/.credentials.json") { DataStream = data });
        }
        return stream.ToArray();
    }

    private static ClaudeAuthFile Credentials(string generation) => Contracts.Create(
        "recovery-fixture-account", "access-" + generation, "refresh-" + generation, email: "recovery@example.invalid");

    private static void Write(string path, ClaudeAuthFile auth) => WriteFixtureBytes(path, JsonSerializer.SerializeToUtf8Bytes(auth));

    private static void WriteFixtureBytes(string path, byte[] bytes)
    {
        if (File.Exists(path)) File.Delete(path);
        WindowsPathSafety.WritePrivateBytes(path, bytes);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
