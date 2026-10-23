using System.Security.AccessControl;
using System.Security.Principal;

namespace ClaudeSyncBar.Windows.Core;

/// <summary>Windows implementation of the shared private-file contract: owner-only ACLs and no reparse points.</summary>
internal static class WindowsPathSafety
{
    // The installed app root is trusted by the loader. Reject links inside Runtime.
    public static void EnsureBundledFile(string path, string runtimeDirectory)
    {
        var root = Path.GetFullPath(runtimeDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ClaudeSyncBarException("앱에 포함된 파일 경로가 Runtime 영역 밖에 있습니다.");
        if (!File.Exists(fullPath)) throw new ClaudeSyncBarException("앱 런타임 파일이 없습니다.");
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new ClaudeSyncBarException("앱에 포함된 파일이 일반 파일이 아닙니다.");
        for (var directory = Path.GetDirectoryName(fullPath)!; ; directory = Path.GetDirectoryName(directory)!)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new ClaudeSyncBarException("앱 Runtime 내부에 연결 경로가 있습니다.");
            if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    // Permissions are set on the empty new file before any credential bytes are written.
    public static void WritePrivateBytes(string path, byte[] contents, SecurityIdentifier? additionalReader = null)
    {
        EnsureFile(path, "비공개 파일");
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new ClaudeSyncBarException("Windows 사용자 SID를 확인하지 못했습니다.");
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        if (additionalReader is not null)
            security.AddAccessRule(new FileSystemAccessRule(additionalReader, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        using var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl,
            FileShare.None, 4096, FileOptions.WriteThrough, security);
        stream.Write(contents);
        stream.Flush(true);
    }

    public static void EnsureSafeAncestors(string path)
    {
        for (var parent = Directory.GetParent(Path.GetFullPath(path)); parent is not null; parent = parent.Parent)
        {
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(parent))
                throw new ClaudeSyncBarException($"저장 경로에 재분석 지점이 있습니다: {parent.FullName}");
        }
    }

    // OneDrive marks known folders as cloud reparse points; those are the user's own folders, not links.
    private static bool IsLink(DirectoryInfo directory)
    {
        try { return directory.LinkTarget is not null; }
        catch (IOException) { return true; }
    }

    public static byte[] ReadPrivateFile(string path, string description, long maximumBytes, SecurityIdentifier? additionalReader = null)
    {
        EnsurePrivateFile(path, description, maximumBytes, additionalReader);
        if (!File.Exists(path)) return [];
        var contents = File.ReadAllBytes(path);
        if (contents.LongLength > maximumBytes)
            throw new ClaudeSyncBarException($"{description} 크기가 안전 한도를 초과했습니다.");
        return contents;
    }

    /// <summary>
    /// A file another program keeps for the user, such as Claude Code's login. It
    /// inherits its folder's ACL, and other tools add readers there (the Codex
    /// sandbox group does), so other readers are accepted; other writers are not.
    /// </summary>
    public static byte[] ReadOwnerWritableFile(string path, string description, long maximumBytes)
    {
        EnsureFileAccess(path, description, maximumBytes, null, othersMayRead: true);
        if (!File.Exists(path)) return [];
        var contents = File.ReadAllBytes(path);
        if (contents.LongLength > maximumBytes)
            throw new ClaudeSyncBarException($"{description} 크기가 안전 한도를 초과했습니다.");
        return contents;
    }

    public static void EnsurePrivateFile(string path, string description, long maximumBytes, SecurityIdentifier? additionalReader = null) =>
        EnsureFileAccess(path, description, maximumBytes, additionalReader, othersMayRead: false);

    private const FileSystemRights ChangeRights = FileSystemRights.Write | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static void EnsureFileAccess(string path, string description, long maximumBytes, SecurityIdentifier? additionalReader, bool othersMayRead)
    {
        EnsureFile(path, description);
        if (!File.Exists(path)) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length < 0 || info.Length > maximumBytes)
                throw new ClaudeSyncBarException($"{description} 크기가 안전 한도를 초과했습니다.");
            var security = info.GetAccessControl();
            using var currentIdentity = WindowsIdentity.GetCurrent();
            var currentUser = currentIdentity.User;
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (currentUser is null || owner is null || !IsOwnedByCurrentIdentity(currentIdentity, currentUser, owner))
                throw new ClaudeSyncBarException($"{description} 소유자가 현재 Windows 사용자와 달라 안전하지 않습니다.");
            var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
            if (descriptor.DiscretionaryAcl is null)
                throw new ClaudeSyncBarException($"{description}에 접근 제한이 없어 안전하지 않습니다.");
            var allowedReaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                currentUser.Value,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            };
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (additionalReader is not null && rule.IdentityReference.Equals(additionalReader) && rule.AccessControlType == AccessControlType.Allow)
                {
                    if ((rule.FileSystemRights & ~(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)) != 0)
                        throw new ClaudeSyncBarException($"{description}에 허용 범위를 넘는 권한이 있습니다.");
                    continue;
                }
                if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier identity
                    || allowedReaders.Contains(identity.Value)) continue;
                if ((rule.FileSystemRights & ChangeRights) != 0)
                    throw new ClaudeSyncBarException($"{description}을(를) 다른 사용자가 변경할 수 있어 안전하지 않습니다.");
                if (!othersMayRead && (rule.FileSystemRights & FileSystemRights.ReadData) != 0)
                    throw new ClaudeSyncBarException($"{description}에 다른 사용자 읽기 권한이 있어 안전하지 않습니다.");
            }
        }
        catch (ClaudeSyncBarException) { throw; }
        catch (UnauthorizedAccessException error) { throw new ClaudeSyncBarException($"{description} 보안 정보를 확인하지 못했습니다: {error.Message}"); }
        catch (IdentityNotMappedException error) { throw new ClaudeSyncBarException($"{description} 소유자를 확인하지 못했습니다: {error.Message}"); }
    }

    public static void EnsureDirectory(string path, string description)
    {
        EnsureSafeAncestors(path);
        if (TryGetAttributes(path, out var attributes))
        {
            if ((attributes & FileAttributes.Directory) == 0) throw new ClaudeSyncBarException($"{description}이 디렉터리가 아닙니다.");
            if ((attributes & FileAttributes.ReparsePoint) != 0 && new DirectoryInfo(path).LinkTarget is not null)
                throw new ClaudeSyncBarException($"{description}의 연결 경로를 거부했습니다.");
            return;
        }
        Directory.CreateDirectory(path);
        if (new DirectoryInfo(path).LinkTarget is not null)
            throw new ClaudeSyncBarException($"생성된 {description}이 안전하지 않습니다.");
    }

    /// <summary>
    /// The Claude Code directory is the user's own; on Windows its ACL is inherited
    /// from the profile, which only the user and administrators can write. Links are refused.
    /// </summary>
    public static void EnsureOwnerWritableOnly(string path, string description)
    {
        EnsureSafeAncestors(path);
        if (!Directory.Exists(path)) return;
        if (new DirectoryInfo(path).LinkTarget is not null)
            throw new ClaudeSyncBarException(description + "이 안전한 디렉터리가 아닙니다.");
    }

    // External CLI and browser children create their own files. Give them a
    // private inheritable directory ACL before launching them.
    public static void EnsurePrivateDirectory(string path, string description)
    {
        EnsureDirectory(path, description);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new ClaudeSyncBarException("Windows 사용자 SID를 확인하지 못했습니다.");
        var directory = new DirectoryInfo(path);
        var existing = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var rules = existing.GetAccessRules(true, true, typeof(SecurityIdentifier));
        // Reapplying an inheritable DACL propagates through the children; only write when it differs.
        if (user.Equals(existing.GetOwner(typeof(SecurityIdentifier)))
            && existing.AreAccessRulesProtected
            && rules.Count == 1
            && rules[0] is FileSystemAccessRule rule
            && !rule.IsInherited
            && rule.IdentityReference.Equals(user)
            && rule.AccessControlType == AccessControlType.Allow
            && rule.FileSystemRights == FileSystemRights.FullControl
            && rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)
            && rule.PropagationFlags == PropagationFlags.None)
            return;
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    public static void EnsureFile(string path, string description)
    {
        EnsureSafeAncestors(path);
        if (!TryGetAttributes(path, out var attributes)) return;
        if ((attributes & FileAttributes.Directory) != 0) throw new ClaudeSyncBarException($"{description}이 파일이 아닙니다.");
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ClaudeSyncBarException($"{description}의 재분석 지점을 거부했습니다.");
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool IsOwnedByCurrentIdentity(WindowsIdentity currentIdentity, SecurityIdentifier currentUser, SecurityIdentifier owner)
    {
        if (owner.Equals(currentUser)) return true;
        // An elevated token owns new files as BUILTIN\Administrators; accept that only while elevated.
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        return owner.Equals(administrators) && new WindowsPrincipal(currentIdentity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
