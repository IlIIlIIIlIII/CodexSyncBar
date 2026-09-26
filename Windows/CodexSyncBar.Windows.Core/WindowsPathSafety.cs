using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CodexSyncBar.Windows.Core;

internal static class WindowsPathSafety
{
    // The installed app root is trusted by the loader and may itself be an
    // OS-managed MSIX reparse point. Reject links inside Runtime, without
    // applying private user-state ancestor rules to that package root.
    public static void EnsureBundledFile(string path, string runtimeDirectory)
    {
        var root = Path.GetFullPath(runtimeDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new CodexSyncBarException("앱에 포함된 파일 경로가 Runtime 영역 밖에 있습니다.");
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new CodexSyncBarException("앱에 포함된 파일이 일반 파일이 아닙니다.");
        for (var directory = Path.GetDirectoryName(fullPath)!; ; directory = Path.GetDirectoryName(directory)!)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new CodexSyncBarException("앱 Runtime 내부에 연결 경로가 있습니다.");
            if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    // Establish permissions on an empty new file before any credential bytes
    // are written. Never relax an existing file's security during replacement.
    public static void WritePrivateBytes(string path, byte[] contents, SecurityIdentifier? additionalReader = null)
    {
        EnsureFile(path, "비공개 파일");
        EnsureSafeAncestors(path);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new CodexSyncBarException("Windows 사용자 SID를 확인하지 못했습니다.");
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
        else
        {
            using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            stream.Write(contents);
            stream.Flush(true);
        }
    }

    public static void EnsureSafeAncestors(string path)
    {
        for (var parent = Directory.GetParent(Path.GetFullPath(path)); parent is not null; parent = parent.Parent)
        {
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new CodexSyncBarException($"저장 경로에 재분석 지점이 있습니다: {parent.FullName}");
        }
    }

    public static byte[] ReadPrivateFile(
        string path,
        string description,
        long maximumBytes,
        SecurityIdentifier? additionalReader = null)
    {
        EnsurePrivateFile(path, description, maximumBytes, additionalReader);
        if (!File.Exists(path))
        {
            return [];
        }

        var contents = File.ReadAllBytes(path);
        if (contents.LongLength > maximumBytes)
        {
            throw new CodexSyncBarException(
                $"{description} 크기가 안전 한도를 초과했습니다: {path}");
        }

        return contents;
    }

    public static void EnsurePrivateFile(
        string path,
        string description,
        long maximumBytes,
        SecurityIdentifier? additionalReader = null)
    {
        EnsureFile(path, description);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length < 0 || info.Length > maximumBytes)
            {
                throw new CodexSyncBarException(
                    $"{description} 크기가 안전 한도를 초과했습니다: {path}");
            }

            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var security = new FileInfo(path).GetAccessControl();
            using var currentIdentity = WindowsIdentity.GetCurrent();
            var currentUser = currentIdentity.User;
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (currentUser is null
                || owner is null
                || !IsOwnedByCurrentIdentity(currentIdentity, currentUser, owner))
            {
                throw new CodexSyncBarException(
                    $"{description} 소유자가 현재 Windows 사용자와 달라 안전하지 않습니다: {path}");
            }

            var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
            if (descriptor.DiscretionaryAcl is null)
                throw new CodexSyncBarException($"{description}에 접근 제한이 없어 안전하지 않습니다: {path}");

            var allowedReaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                currentUser.Value,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            };
            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier)))
            {
                // The active CLI file may already be readable by Codex's local
                // sandbox group. This exception must never grant it write access.
                if (additionalReader is not null && rule.IdentityReference.Equals(additionalReader)
                    && rule.AccessControlType == AccessControlType.Allow)
                {
                    if ((rule.FileSystemRights & ~(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)) != 0)
                        throw new CodexSyncBarException($"{description}에 Codex 샌드박스의 읽기 범위를 넘는 권한이 있습니다: {path}");
                    continue;
                }
                if (rule.AccessControlType == AccessControlType.Allow
                    && rule.IdentityReference is SecurityIdentifier identity
                    && !allowedReaders.Contains(identity.Value)
                    && (rule.FileSystemRights & FileSystemRights.ReadData) != 0)
                {
                    throw new CodexSyncBarException(
                        $"{description}에 다른 사용자 읽기 권한이 있어 안전하지 않습니다: {path}");
                }
            }
        }
        catch (CodexSyncBarException)
        {
            throw;
        }
        catch (UnauthorizedAccessException error)
        {
            throw new CodexSyncBarException(
                $"{description} 보안 정보를 확인하지 못했습니다: {error.Message}");
        }
        catch (IdentityNotMappedException error)
        {
            throw new CodexSyncBarException(
                $"{description} 소유자를 확인하지 못했습니다: {error.Message}");
        }
    }

    public static void EnsureDirectory(string path, string description)
    {
        EnsureSafeAncestors(path);
        if (TryGetAttributes(path, out var attributes))
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new CodexSyncBarException($"{description}의 재분석 지점을 거부했습니다: {path}");
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new CodexSyncBarException($"{description}이 디렉터리가 아닙니다: {path}");
            }

            return;
        }

        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new CodexSyncBarException($"생성된 {description}이 안전하지 않습니다: {path}");
        }
    }

    // External CLI/Chrome children create their own files. Give them a private
    // inheritable directory ACL before launching, rather than fixing auth.json
    // after a child has already written credential bytes.
    public static void EnsurePrivateDirectory(string path, string description)
    {
        EnsureDirectory(path, description);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new CodexSyncBarException("Windows 사용자 SID를 확인하지 못했습니다.");
            var directory = new DirectoryInfo(path);
            var existing = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var rules = existing.GetAccessRules(true, true, typeof(SecurityIdentifier));
            // Reapplying an inheritable DACL propagates through the directory's
            // children. Reads call this frequently; validate every time, but
            // only write when the owner or exact private ACL actually differs.
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
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static void EnsureFile(string path, string description)
    {
        EnsureSafeAncestors(path);
        if (!TryGetAttributes(path, out var attributes))
        {
            return;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new CodexSyncBarException($"{description}이 파일이 아닙니다: {path}");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new CodexSyncBarException($"{description}의 재분석 지점을 거부했습니다: {path}");
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsOwnedByCurrentIdentity(
        WindowsIdentity currentIdentity,
        SecurityIdentifier currentUser,
        SecurityIdentifier owner)
    {
        if (owner.Equals(currentUser))
        {
            return true;
        }

        // An elevated administrator token uses BUILTIN\Administrators as the
        // default owner for newly-created files. Accept that owner only while
        // the current token is actively elevated; a normal user's disabled
        // administrator group membership is not sufficient. Broad read ACLs
        // are still rejected by the caller below.
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        return owner.Equals(administrators)
            && new WindowsPrincipal(currentIdentity)
                .IsInRole(WindowsBuiltInRole.Administrator);
    }
}
