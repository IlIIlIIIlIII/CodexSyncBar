using System.Security.AccessControl;
using System.Security.Principal;

namespace CodexSyncBar.Windows.Core;

internal static class WindowsPathSafety
{
    public static byte[] ReadPrivateFile(
        string path,
        string description,
        long maximumBytes)
    {
        EnsurePrivateFile(path, description, maximumBytes);
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
        long maximumBytes)
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
            var currentUser = WindowsIdentity.GetCurrent().User;
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (currentUser is null || owner is null || !owner.Equals(currentUser))
            {
                throw new CodexSyncBarException(
                    $"{description} 소유자가 현재 Windows 사용자와 달라 안전하지 않습니다: {path}");
            }

            var broadPrincipals = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value,
            };
            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow
                    && rule.IdentityReference is SecurityIdentifier identity
                    && broadPrincipals.Contains(identity.Value)
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

    public static void EnsureFile(string path, string description)
    {
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
}
