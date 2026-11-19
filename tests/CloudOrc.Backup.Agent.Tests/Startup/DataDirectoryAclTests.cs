using System.Security.AccessControl;
using System.Security.Principal;
using CloudOrc.Backup.Agent.Startup;

namespace CloudOrc.Backup.Agent.Tests.Startup;

/// <summary>Windows: the data directory (credential, device key, state) is SYSTEM + Administrators only.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DataDirectoryAclTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudorc-acl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void GrantUsersWrite(string dir)
    {
        var info = new DirectoryInfo(dir);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.WriteData | FileSystemRights.AppendData, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }

    [WindowsFact]
    public void Harden_RemovesInheritance_AndLeavesOnlySystemAndAdministrators()
    {
        Assert.Null(DataDirectoryAcl.Harden(_root));

        var acl = new DirectoryInfo(_root).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        var sids = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => ((SecurityIdentifier)r.IdentityReference).Value).Distinct().OrderBy(v => v).ToList();
        Assert.Equal(new[] { "S-1-5-18", "S-1-5-32-544" }.OrderBy(v => v), sids);
        Assert.True(DataDirectoryAcl.IsWritableOnlyByPrivileged(_root, out _));
    }

    [WindowsFact]
    public void IsHardened_IsFalseBeforeHarden_AndTrueAfter_SoLaterStartsSkipTheSlowTreeRewrite()
    {
        Directory.CreateDirectory(_root);
        Assert.False(DataDirectoryAcl.IsHardened(_root));
        Assert.False(DataDirectoryAcl.IsHardened(Path.Combine(_root, "missing")));

        Assert.Null(DataDirectoryAcl.Harden(_root));
        Assert.True(DataDirectoryAcl.IsHardened(_root));

        GrantUsersWrite(_root);
        Assert.False(DataDirectoryAcl.IsHardened(_root));
    }

    [WindowsFact]
    public void UsersWriteAccess_IsDetected()
    {
        DataDirectoryAcl.Harden(_root);
        GrantUsersWrite(_root);
        Assert.False(DataDirectoryAcl.IsWritableOnlyByPrivileged(_root, out var offender));
        Assert.Equal("S-1-5-32-545", offender);
    }
}
