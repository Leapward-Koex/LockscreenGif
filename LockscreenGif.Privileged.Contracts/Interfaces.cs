namespace LockscreenGif.Services.Lockscreen
{
    public interface ICachePermissionSession : IAsyncDisposable
    {
        Task<int> GrantAsync(string path, bool write, CancellationToken cancellationToken);
    }
}

namespace LockscreenGif.Privileged
{
    public interface IPrivilegedOperationSession : LockscreenGif.Services.Lockscreen.ICachePermissionSession
    {
        Task StartTraceAsync(TraceScope scope, CancellationToken token);
        Task<TraceBatch> ReadTraceAsync(CancellationToken token);
        Task StopTraceAsync(CancellationToken token);
    }
}
