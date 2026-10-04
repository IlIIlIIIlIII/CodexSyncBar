using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CodexSyncBar.Windows.Core;

/// <summary>AES-GCM envelope; its master key is held by the login user's Secret Service.</summary>
public sealed class WindowsSecretStore(WindowsPaths paths)
{
    private static readonly byte[] Magic = "CSBL1"u8.ToArray();
    public void Save(string secret, string namespaceKey)
    {
        if (string.IsNullOrEmpty(secret)) { Delete(namespaceKey); return; }
        var destination = SecretPath(namespaceKey);
        var plaintext = Encoding.UTF8.GetBytes(secret);
        try
        {
            var bytes = Protect(plaintext, namespaceKey);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { WindowsPathSafety.WritePrivateBytes(temporary, bytes); File.Move(temporary, destination, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public string? Read(string namespaceKey)
    {
        var path = SecretPath(namespaceKey);
        if (!File.Exists(path)) return null;
        var bytes = WindowsPathSafety.ReadPrivateFile(path, "암호화된 비밀", 32 * 1024 * 1024);
        var plaintext = Unprotect(bytes, namespaceKey);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public void Delete(string namespaceKey)
    {
        var path = SecretPath(namespaceKey);
        if (File.Exists(path)) File.Delete(path);
    }
    private string SecretPath(string namespaceKey)
    {
        if (string.IsNullOrWhiteSpace(namespaceKey) || namespaceKey.Any(c => !char.IsLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new CodexSyncBarException("비밀 저장소 식별자가 올바르지 않습니다.");
        paths.EnsureDirectories();
        var directory = Path.Combine(paths.StateRoot, "secrets");
        WindowsPathSafety.EnsurePrivateDirectory(directory, "비밀 저장소");
        var path = Path.Combine(directory, namespaceKey + ".bin");
        WindowsPathSafety.EnsurePrivateFile(path, "암호화된 비밀", 32 * 1024 * 1024);
        return path;
    }
    internal static byte[] Protect(byte[] plaintext, string namespaceKey)
    {
        var key = LinuxSecretKeyProvider.GetKey(create: true);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(namespaceKey));
            return [.. Magic, .. nonce, .. tag, .. ciphertext];
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    internal static byte[] Unprotect(byte[] protectedBytes, string namespaceKey)
    {
        if (protectedBytes.Length < 33 || !protectedBytes.AsSpan(0, 5).SequenceEqual(Magic))
            throw new CryptographicException("이 환경에서 해독할 수 없는 인증입니다. 계정을 다시 로그인해 주세요.");
        var key = LinuxSecretKeyProvider.GetKey(create: false);
        try
        {
            var plaintext = new byte[protectedBytes.Length - 33];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(protectedBytes.AsSpan(5, 12), protectedBytes.AsSpan(33), protectedBytes.AsSpan(17, 16), plaintext, Encoding.UTF8.GetBytes(namespaceKey));
            return plaintext;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}

internal static class LinuxSecretKeyProvider
{
    private static readonly object Gate = new();
    private static Func<byte[]>? _testProvider;
    internal static IDisposable OverrideForTests(Func<byte[]> provider)
    {
        lock (Gate)
        {
            var previous = _testProvider;
            _testProvider = provider;
            return new Reset(previous, provider);
        }
    }
    private sealed class Reset(Func<byte[]>? previous, Func<byte[]> installed) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock (Gate)
            {
                if (_disposed) return;
                if (!ReferenceEquals(_testProvider, installed)) throw new InvalidOperationException("Test key scopes must be disposed in reverse order.");
                _testProvider = previous;
                _disposed = true;
            }
        }
    }
    internal static byte[] GetKey(bool create)
    {
        lock (Gate)
        {
            if (_testProvider is not null)
            {
                var test = _testProvider();
                if (test.Length != 32) throw new CryptographicException("Test key must contain 32 bytes.");
                return test.ToArray();
            }
            // A cross-process lock prevents two first launches from replacing one another's key.
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(runtime) || !Path.IsPathFullyQualified(runtime))
                throw new CodexSyncBarException("로그인 세션의 XDG_RUNTIME_DIR가 필요합니다.");
            WindowsPathSafety.EnsurePrivateDirectory(runtime, "로그인 런타임");
            var lockPath = Path.Combine(runtime, "codex-syncbar-secret-key.lock");
            WindowsPathSafety.EnsureFile(lockPath, "비밀 키 잠금");
            using var keyLock = new FileStream(lockPath, new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.ReadWrite, UnixCreateMode = WindowsPathSafety.PrivateFileMode });
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                try { keyLock.Lock(0, 1); break; }
                catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(50); }
            }
            try
            {
                var lookup = Run(["lookup", "application", "codex-syncbar", "purpose", "vault-key-v1"], null);
                if (lookup.ExitCode == 0 && !string.IsNullOrWhiteSpace(lookup.Output)) return Decode(lookup.Output);
                // A missing item has no diagnostic. A service/unlock error must never
                // create a replacement master key and orphan existing encrypted profiles.
                if (lookup.ExitCode != 1 || !string.IsNullOrWhiteSpace(lookup.Error))
                    throw new CodexSyncBarException("로그인 키링을 잠금 해제한 뒤 다시 시도해 주세요.");
                if (!create) throw new CodexSyncBarException("로그인 키링의 SyncBar 비밀 키를 열지 못했습니다.");
                var key = RandomNumberGenerator.GetBytes(32);
                var stored = Run(["store", "--label=Codex SyncBar vault", "application", "codex-syncbar", "purpose", "vault-key-v1"], Convert.ToBase64String(key));
                if (stored.ExitCode != 0) { CryptographicOperations.ZeroMemory(key); throw new CodexSyncBarException("로그인 키링을 잠금 해제한 뒤 다시 시도해 주세요."); }
                var verified = Run(["lookup", "application", "codex-syncbar", "purpose", "vault-key-v1"], null);
                if (verified.ExitCode != 0 || !CryptographicOperations.FixedTimeEquals(key, Decode(verified.Output)))
                { CryptographicOperations.ZeroMemory(key); throw new CodexSyncBarException("키링의 비밀 키 저장을 확인하지 못했습니다."); }
                return key;
            }
            finally { keyLock.Unlock(0, 1); }
        }
    }
    private static byte[] Decode(string text)
    {
        try { var key = Convert.FromBase64String(text.Trim()); if (key.Length == 32) return key; }
        catch (FormatException) { }
        throw new CryptographicException("키링 비밀 키 형식이 올바르지 않습니다.");
    }
    private static (int ExitCode, string Output, string Error) Run(string[] arguments, string? input)
    {
        var tool = "/usr/bin/secret-tool";
        var helper = Path.Combine(AppContext.BaseDirectory, "Runtime", "codex-syncbar-secret-tool");
        var useHelper = !File.Exists(tool);
        if (useHelper)
        {
            if (!File.Exists("/usr/bin/python3") || !File.Exists(helper))
                throw new CodexSyncBarException("GNOME 키링을 사용하려면 libsecret-tools 또는 gir1.2-secret-1 패키지가 필요합니다.");
            tool = "/usr/bin/python3";
        }
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        if (useHelper) { start.ArgumentList.Add("-I"); start.ArgumentList.Add(helper); }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new CodexSyncBarException("로그인 키링을 열지 못했습니다.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (input is not null) process.StandardInput.Write(input);
        process.StandardInput.Close();
        if (!process.WaitForExit(60000)) { process.Kill(); throw new CodexSyncBarException("로그인 키링 응답 시간이 초과됐습니다."); }
        Task.WaitAll(output, error);
        return (process.ExitCode, output.Result, error.Result);
    }
}
