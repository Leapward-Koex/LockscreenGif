namespace LockscreenGif.Services.Lockscreen
{
    public interface ICachePermissionSession : IAsyncDisposable
    {
        Task<int> GrantAsync(string path, bool write, CancellationToken cancellationToken);
    }
}

namespace LockscreenGif.Privileged
{
    public interface IWindowsImageFeatureSession
    {
        Task<WindowsImageFeatureResult> DisableWindowsImageFeatureAsync(
            CancellationToken token,
            uint featureId = WindowsImageFeature.DefaultFeatureId
        ) =>
            Task.FromResult(
                new WindowsImageFeatureResult
                {
                    FeatureId = featureId,
                    Outcome = "Failed",
                    Error = "This helper session cannot configure the Windows lock-screen animation feature.",
                }
            );

        Task<WindowsImageFeatureResult> EnableWindowsImageFeatureAsync(
            CancellationToken token,
            uint featureId = WindowsImageFeature.DefaultFeatureId
        ) =>
            Task.FromResult(
                new WindowsImageFeatureResult
                {
                    FeatureId = featureId,
                    DesiredState = "Enabled",
                    Outcome = "Failed",
                    Error = "This helper session cannot configure the Windows lock-screen animation feature.",
                }
            );
    }

    public interface IPrivilegedOperationSession : LockscreenGif.Services.Lockscreen.ICachePermissionSession, IWindowsImageFeatureSession
    {
        Task StartTraceAsync(TraceScope scope, CancellationToken token);
        Task<TraceBatch> ReadTraceAsync(CancellationToken token);
        Task StopTraceAsync(CancellationToken token);
    }
}
