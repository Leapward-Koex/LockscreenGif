using System.Security.Principal;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Lockscreen;

namespace LockscreenGif.Services.Diagnostics;

public sealed class PrivilegedSessionFactory
{
    private readonly Func<string, IPrivilegedOperationSession>? _create;

    public PrivilegedSessionFactory() { }

    public PrivilegedSessionFactory(Func<string, IPrivilegedOperationSession> create) => _create = create;

    public IPrivilegedOperationSession Create(string cacheDirectory)
    {
        if (_create is not null)
        {
            return _create(cacheDirectory);
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new CachePermissionSession(cacheDirectory, identity.User!.Value);
    }
}
