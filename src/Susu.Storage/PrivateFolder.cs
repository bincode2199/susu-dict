using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Susu.Storage;

/// <summary>
/// Folders that hold key material or its staged copies (the import stage and restore point, the config journal) are readable only by the
/// current user and SYSTEM: inheritance from the profile is cut and no other account is listed (F17.1 open item 3, ARCHITECTURE 8.1).
/// Contents are DPAPI ciphertext as well; this is a second layer, not a replacement. A no-op off Windows.
/// </summary>
public static class PrivateFolder
{
    /// <summary>Creates the folder if needed and applies the restricted ACL. Idempotent. Children inherit it.</summary>
    public static void Ensure(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows()) Restrict(path);
    }

    [SupportedOSPlatform("windows")]
    private static void Restrict(string path)
    {
        var info = new DirectoryInfo(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var user = WindowsIdentity.GetCurrent().User;
        if (user is not null) security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>The SIDs with an explicit or inherited allow rule on the folder, and whether it still inherits. For tests and diagnostics.</summary>
    [SupportedOSPlatform("windows")]
    public static (bool Inherits, IReadOnlyList<string> Sids) Describe(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Where(r => r.AccessControlType == AccessControlType.Allow);
        return (!security.AreAccessRulesProtected, [.. rules.Select(r => r.IdentityReference.Value).Distinct()]);
    }
}
