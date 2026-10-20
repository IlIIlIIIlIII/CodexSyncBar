using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeSyncBar.Windows.Core;

/// <summary>
/// Secrets encrypted with the current Windows user's DPAPI key. They are never
/// written as plaintext to the configuration or passed in process arguments.
/// </summary>
public sealed class WindowsSecretStore(WindowsPaths paths)
{
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
        byte[] plaintext;
        try { plaintext = Unprotect(bytes, namespaceKey); }
        catch (CryptographicException) { throw new ClaudeSyncBarException("Windows 비밀 저장소를 해독하지 못했습니다. 계정을 다시 로그인해 주세요."); }
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
        if (string.IsNullOrWhiteSpace(namespaceKey) || namespaceKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ClaudeSyncBarException("비밀 저장소 식별자가 올바르지 않습니다.");
        paths.EnsureDirectories();
        var directory = Path.Combine(paths.StateRoot, "secrets");
        WindowsPathSafety.EnsurePrivateDirectory(directory, "비밀 저장소");
        var path = Path.Combine(directory, namespaceKey + ".bin");
        WindowsPathSafety.EnsurePrivateFile(path, "암호화된 비밀", 32 * 1024 * 1024);
        return path;
    }

    // The namespace is bound into the entropy, so a secret copied under another name does not decrypt.
    internal static byte[] Protect(byte[] plaintext, string namespaceKey) => Transform(plaintext, namespaceKey, protect: true);

    internal static byte[] Unprotect(byte[] protectedBytes, string namespaceKey) => Transform(protectedBytes, namespaceKey, protect: false);

    private static byte[] Transform(byte[] input, string namespaceKey, bool protect)
    {
        var entropyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(namespaceKey));
        var source = Pin(input);
        var entropy = Pin(entropyBytes);
        try
        {
            Blob output;
            var ok = protect
                ? CryptProtectData(ref source, "Claude SyncBar secret", ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastPInvokeError());
            try
            {
                var bytes = new byte[output.Size];
                Marshal.Copy(output.Data, bytes, 0, output.Size);
                return bytes;
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                {
                    // Plaintext from Unprotect is wiped before the buffer goes back to the system.
                    if (!protect) Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
                    LocalFree(output.Data);
                }
            }
        }
        finally
        {
            Free(source);
            Free(entropy);
        }
    }

    private const uint UiForbidden = 0x1;

    private static Blob Pin(byte[] bytes)
    {
        var blob = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length)) };
        Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }

    private static void Free(Blob blob)
    {
        if (blob.Data == IntPtr.Zero) return;
        Marshal.Copy(new byte[blob.Size], 0, blob.Data, blob.Size);
        Marshal.FreeHGlobal(blob.Data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob dataIn, string description, ref Blob optionalEntropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, ref Blob optionalEntropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
