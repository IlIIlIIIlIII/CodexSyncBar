using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CodexSyncBar.Windows.Core;

/// <summary>
/// Windows equivalent of the macOS device-only Keychain entries. Secrets are
/// encrypted with the current user's DPAPI key and never written as plaintext
/// to the JSON configuration or process arguments.
/// </summary>
public sealed class WindowsSecretStore
{
    private readonly WindowsPaths _paths;

    public WindowsSecretStore(WindowsPaths paths)
    {
        _paths = paths;
    }

    public void Save(string secret, string namespaceKey)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI is only available on Windows.");
        }

        if (string.IsNullOrEmpty(secret))
        {
            Delete(namespaceKey);
            return;
        }

        var destination = SecretPath(namespaceKey);
        EnsureSafeSecretFile(destination);
        var protectedBytes = Protect(Encoding.UTF8.GetBytes(secret), namespaceKey);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        WindowsPathSafety.WritePrivateBytes(temporary, protectedBytes);
        try
        {
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public string? Read(string namespaceKey)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI is only available on Windows.");
        }

        var path = SecretPath(namespaceKey);
        EnsureSafeSecretFile(path);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var plaintext = Unprotect(File.ReadAllBytes(path), namespaceKey);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException error)
        {
            throw new CodexSyncBarException($"Windows 비밀 저장소를 해독하지 못했습니다: {error.Message}");
        }
    }

    public void Delete(string namespaceKey)
    {
        var path = SecretPath(namespaceKey);
        EnsureSafeSecretFile(path);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string SecretPath(string namespaceKey)
    {
        if (string.IsNullOrWhiteSpace(namespaceKey)
            || namespaceKey.Any(character => !char.IsLetterOrDigit(character)
                && character is not ('.' or '-' or '_')))
        {
            throw new CodexSyncBarException("비밀 저장소 식별자가 올바르지 않습니다.");
        }

        _paths.EnsureDirectories();
        var directory = Path.Combine(_paths.StateRoot, "secrets");
        WindowsPathSafety.EnsureDirectory(directory, "Windows 비밀 저장소 디렉터리");
        return Path.Combine(directory, namespaceKey + ".bin");
    }

    private static void EnsureSafeSecretFile(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new CodexSyncBarException($"Windows 비밀 저장소 경로가 파일이 아닙니다: {path}");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new CodexSyncBarException($"Windows 비밀 저장소 경로가 안전하지 않습니다: {path}");
        }
    }

    internal static byte[] Protect(byte[] plaintext, string namespaceKey)
    {
        var input = new DataBlob(plaintext);
        var entropy = new DataBlob(SHA256.HashData(Encoding.UTF8.GetBytes(namespaceKey)));
        try
        {
            if (!CryptProtectData(
                    ref input.Native,
                    "Codex SyncBar secret",
                    ref entropy.Native,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    out var output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            return CopyAndFree(output);
        }
        finally
        {
            input.Dispose();
            entropy.Dispose();
        }
    }

    internal static byte[] Unprotect(byte[] protectedBytes, string namespaceKey)
    {
        var input = new DataBlob(protectedBytes);
        var entropy = new DataBlob(SHA256.HashData(Encoding.UTF8.GetBytes(namespaceKey)));
        try
        {
            if (!CryptUnprotectData(
                    ref input.Native,
                    IntPtr.Zero,
                    ref entropy.Native,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    out var output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            return CopyAndFree(output);
        }
        finally
        {
            input.Dispose();
            entropy.Dispose();
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref NativeDataBlob dataIn,
        string description,
        ref NativeDataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out NativeDataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref NativeDataBlob dataIn,
        IntPtr description,
        ref NativeDataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out NativeDataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static byte[] CopyAndFree(NativeDataBlob blob)
    {
        if (blob.Data == IntPtr.Zero || blob.Size < 0)
        {
            throw new CryptographicException("Windows DPAPI가 유효하지 않은 데이터를 반환했습니다.");
        }

        var bytes = new byte[blob.Size];
        if (blob.Size > 0)
        {
            Marshal.Copy(blob.Data, bytes, 0, blob.Size);
        }

        LocalFree(blob.Data);
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeDataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private sealed class DataBlob : IDisposable
    {
        public DataBlob(byte[] bytes)
        {
            Native = new NativeDataBlob
            {
                Size = bytes.Length,
                Data = Marshal.AllocHGlobal(bytes.Length),
            };
            Marshal.Copy(bytes, 0, Native.Data, bytes.Length);
        }

        public NativeDataBlob Native;

        public byte[] ToArrayAndFree()
        {
            var bytes = new byte[Native.Size];
            Marshal.Copy(Native.Data, bytes, 0, Native.Size);
            LocalFree(Native.Data);
            Native.Data = IntPtr.Zero;
            Native.Size = 0;
            return bytes;
        }

        public void Dispose()
        {
            if (Native.Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Native.Data);
                Native.Data = IntPtr.Zero;
            }
        }
    }
}
