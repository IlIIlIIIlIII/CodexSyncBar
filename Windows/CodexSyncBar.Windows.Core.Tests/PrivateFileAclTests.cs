using System.Security.AccessControl;
using System.Security.Principal;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class PrivateFileAclTests
{
    [Theory]
    [InlineData("reader")]
    [InlineData("inheritance")]
    [InlineData("unprotected")]
    public void RepeatedDirectoryChecksStillRepairChangedPermissions(string change)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "syncbar-acl-recheck-" + Guid.NewGuid().ToString("N"));
        try
        {
            WindowsPathSafety.EnsurePrivateDirectory(root, "test directory");
            WindowsPathSafety.EnsurePrivateDirectory(root, "test directory");
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User!;
            var directory = new DirectoryInfo(root);
            var altered = directory.GetAccessControl();
            if (change == "reader")
                altered.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), FileSystemRights.ReadData, AccessControlType.Allow));
            else if (change == "inheritance")
                altered.SetAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            else
                altered.SetAccessRuleProtection(false, true);
            directory.SetAccessControl(altered);

            WindowsPathSafety.EnsurePrivateDirectory(root, "test directory");
            var repaired = directory.GetAccessControl();
            Assert.Equal(user, repaired.GetOwner(typeof(SecurityIdentifier)));
            Assert.True(repaired.AreAccessRulesProtected);
            var rule = Assert.Single(repaired.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            Assert.Equal(user, rule.IdentityReference);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
            Assert.False(rule.IsInherited);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [WindowsFact]
    public void UnrelatedNonBroadPrincipalCannotReadPrivateAuth()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var root = Path.Combine(Path.GetTempPath(), "syncbar-acl-unrelated-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var file = Path.Combine(root, "synthetic-auth.json");
            WindowsPathSafety.WritePrivateBytes(file, "{}"u8.ToArray());
            var security = new FileInfo(file).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), FileSystemRights.ReadData, AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(security);
            Assert.Throws<CodexSyncBarException>(() => WindowsPathSafety.EnsurePrivateFile(file, "test auth", 1024));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [WindowsFact]
    public void NormalInheritedSystemAndAdministratorsReadersAreAccepted()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var root = Path.Combine(Path.GetTempPath(), "syncbar-acl-trusted-" + Guid.NewGuid().ToString("N"));
        try
        {
            WindowsPathSafety.EnsurePrivateDirectory(root, "test directory");
            var directorySecurity = new DirectoryInfo(root).GetAccessControl();
            foreach (var principal in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                directorySecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(principal, null),
                    FileSystemRights.ReadData, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).SetAccessControl(directorySecurity);
            var file = Path.Combine(root, "synthetic-auth.json");
            File.WriteAllText(file, "{}");
            WindowsPathSafety.EnsurePrivateFile(file, "test auth", 1024);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
