using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CodexSyncBar.Windows.Core;

/// <summary>POSIX implementation of the shared private-file contract.</summary>
internal static class WindowsPathSafety
{
    internal const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    internal const UnixFileMode PrivateDirectoryMode = PrivateFileMode | UnixFileMode.UserExecute;

    public static void EnsureBundledFile(string path, string runtimeDirectory)
    {
        var root = Path.GetFullPath(runtimeDirectory).TrimEnd('/');
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + "/", StringComparison.Ordinal))
            throw new CodexSyncBarException("앱 런타임 밖의 파일을 거부했습니다.");
        EnsureFile(full, "앱 런타임 파일");
        if (!File.Exists(full)) throw new CodexSyncBarException("앱 런타임 파일이 없습니다.");
    }

    public static void WritePrivateBytes(string path, byte[] contents, SecurityIdentifier? additionalReader = null)
    {
        EnsureFile(path, "비공개 파일");
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = PrivateFileMode, Options = FileOptions.WriteThrough,
        });
        stream.Write(contents);
        stream.Flush(true);
    }

    public static void EnsureSafeAncestors(string path)
    {
        for (var parent = Directory.GetParent(Path.GetFullPath(path)); parent is not null; parent = parent.Parent)
        {
            if (!LinuxFileMetadata.TryRead(parent.FullName, out var info)) continue;
            if (!info.IsDirectory) throw new CodexSyncBarException("저장 경로에 심볼릭 링크 또는 일반 디렉터리가 아닌 경로가 있습니다.");
            // Shared /tmp is allowed only with the sticky bit; every app state root is 0700.
            if ((info.Mode & 0x12) != 0 && (info.Mode & 0x200) == 0)
                throw new CodexSyncBarException("다른 사용자가 변경할 수 있는 저장 경로입니다.");
        }
    }

    public static byte[] ReadPrivateFile(string path, string description, long maximumBytes, SecurityIdentifier? additionalReader = null)
    {
        EnsurePrivateFile(path, description, maximumBytes, additionalReader);
        if (!File.Exists(path)) return [];
        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength > maximumBytes) throw new CodexSyncBarException(description + " 크기가 안전 한도를 초과했습니다.");
        return bytes;
    }

    public static void EnsurePrivateFile(string path, string description, long maximumBytes, SecurityIdentifier? additionalReader = null)
    {
        EnsureFile(path, description);
        if (!LinuxFileMetadata.TryRead(path, out var info)) return;
        if (info.Uid != LinuxFileMetadata.CurrentUid || (info.Mode & 0x3f) != 0 || info.Size > (ulong)maximumBytes)
            throw new CodexSyncBarException(description + "의 소유자·권한 또는 크기가 안전하지 않습니다.");
        if (info.Links != 1) throw new CodexSyncBarException(description + "의 하드 링크를 거부했습니다.");
    }

    public static void EnsureDirectory(string path, string description)
    {
        EnsureSafeAncestors(path);
        if (LinuxFileMetadata.TryRead(path, out var info))
        {
            if (!info.IsDirectory) throw new CodexSyncBarException(description + "이 안전한 디렉터리가 아닙니다.");
            return;
        }
        Directory.CreateDirectory(path, PrivateDirectoryMode);
        if (!LinuxFileMetadata.TryRead(path, out info) || !info.IsDirectory)
            throw new CodexSyncBarException(description + " 생성 결과를 확인하지 못했습니다.");
    }

    public static void EnsurePrivateDirectory(string path, string description)
    {
        EnsureDirectory(path, description);
        if (!LinuxFileMetadata.TryRead(path, out var info) || info.Uid != LinuxFileMetadata.CurrentUid)
            throw new CodexSyncBarException(description + " 소유자가 현재 사용자와 다릅니다.");
        if ((info.Mode & 0x1ff) != 0x1c0) File.SetUnixFileMode(path, PrivateDirectoryMode);
    }

    public static void EnsureFile(string path, string description)
    {
        EnsureSafeAncestors(path);
        if (LinuxFileMetadata.TryRead(path, out var info) && !info.IsRegularFile)
            throw new CodexSyncBarException(description + "이 일반 파일이 아니거나 심볼릭 링크입니다.");
    }
}

internal static class LinuxFileMetadata
{
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct Entry
    {
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(40)] public ulong Size;
        public bool IsDirectory => (Mode & 0xf000) == 0x4000;
        public bool IsRegularFile => (Mode & 0xf000) == 0x8000;
    }
    [DllImport("libc", SetLastError = true)] private static extern int statx(int dirfd, string path, int flags, uint mask, out Entry entry);
    [DllImport("libc")] private static extern uint geteuid();
    internal static uint CurrentUid => geteuid();
    internal static bool TryRead(string path, out Entry entry)
    {
        if (statx(-100, path, 0x100, 0x7ff, out entry) == 0) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error is 2 or 20) return false;
        throw new IOException("파일 소유자와 종류를 확인하지 못했습니다.", new System.ComponentModel.Win32Exception(error));
    }
}
