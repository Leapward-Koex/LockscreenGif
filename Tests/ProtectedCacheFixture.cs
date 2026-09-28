using System.Security.AccessControl;
using System.Security.Principal;

namespace LockscreenGif.Tests;

/// <summary>A private temporary tree with readable cache children beneath protected parents.</summary>
internal sealed class ProtectedCacheFixture : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "LockscreenGif-protected-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Path, string Sddl, bool Directory)> _original = [];
    private readonly SecurityIdentifier _user = WindowsIdentity.GetCurrent().User!;
    public string SystemData { get; }
    public string Parent { get; }
    public string Root { get; }
    public string Folder { get; }
    public string Image { get; }
    public string Link { get; }
    public string Source { get; }

    public ProtectedCacheFixture()
    {
        SystemData = Path.Combine(_base, "SystemData");
        Parent = Path.Combine(SystemData, "synthetic-user");
        Root = Path.Combine(Parent, "ReadOnly");
        Folder = Path.Combine(Root, "LockScreen_A");
        Image = Path.Combine(Folder, "LockScreen.jpg");
        Link = Path.Combine(SystemData, "protected-link");
        Source = Path.Combine(_base, "source.gif");
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Image, "previous image");
        File.WriteAllBytes(Source, Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"));
        try
        {
            Directory.CreateSymbolicLink(Link, Root);
            foreach (var path in new[] { SystemData, Parent, Root, Folder, Image })
            {
                var directory = path != Image;
                FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
                _original.Add((path, acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access), directory));
            }
            // Restrict the outer parents last, after recording every original ACL.
            Restrict(Image, directory: false, read: true);
            Restrict(Folder, directory: true, read: true);
            Restrict(Root, directory: true, read: true);
            Restrict(Parent, directory: true, read: false);
            Restrict(SystemData, directory: true, read: false);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Restrict(string path, bool directory, bool read)
    {
        FileSystemSecurity acl = directory ? new DirectorySecurity() : new FileSecurity();
        // Keep the creator's owner unchanged so its DACL can be restored without elevation.
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(
            new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow)
        );
        if (read)
        {
            acl.AddAccessRule(new(_user, FileSystemRights.Read, AccessControlType.Allow));
        }
        Save(path, acl, directory);
    }

    public void Grant(string path, bool write)
    {
        if (
            !path.Equals(Root, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidOperationException("Fixture grants must remain inside its cache.");
        }
        var directory = path != Image;
        FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        acl.AddAccessRule(new(_user, write ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        Save(path, acl, directory);
    }

    public void GrantParentMetadata()
    {
        var acl = new DirectoryInfo(Parent).GetAccessControl();
        acl.AddAccessRule(new(_user, FileSystemRights.ReadAttributes, AccessControlType.Allow));
        Save(Parent, acl, directory: true);
    }

    private static void Save(string path, FileSystemSecurity acl, bool directory)
    {
        if (directory)
        {
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)acl);
        }
        else
        {
            new FileInfo(path).SetAccessControl((FileSecurity)acl);
        }
    }

    public void Dispose()
    {
        // Fresh descriptors mark the restored sections dirty for SetAccessControl.
        // Restore parents before children so protected traversal cannot block cleanup.
        foreach (var (path, sddl, directory) in _original)
        {
            FileSystemSecurity acl = directory ? new DirectorySecurity() : new FileSecurity();
            acl.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
            Save(path, acl, directory);
        }
        if (Directory.Exists(Link))
        {
            Directory.Delete(Link);
        }
        // This path is created above with a unique name under the temp directory.
        if (!Path.GetFullPath(_base).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unsafe fixture cleanup path.");
        }
        Directory.Delete(_base, recursive: true);
    }
}
