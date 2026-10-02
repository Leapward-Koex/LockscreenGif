using LockscreenGif.Models;
using LockscreenGif.Services;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace LockscreenGif.Contracts.Services;

public interface ILockscreenService
{
    Task<LockscreenApplyResult> ApplyGifAsLockscreenAsync();
    Task<LockscreenApplyResult> ApplyAsync(
        string sourcePath,
        bool useWindowsApi,
        Action<LockscreenApplyEvent>? progress = null,
        CancellationToken cancellationToken = default,
        LockscreenGif.Services.Lockscreen.ICachePermissionSession? permissionSession = null,
        LockscreenSourceKind sourceKind = LockscreenSourceKind.Unknown
    );
    Task<DeleteFilesResult?> RemoveAppliedGif();
    Task WaitForIdleAsync();
    bool IsApplying { get; }
    string CacheDirectory { get; }
    void SetCurrentImage(StorageFile file, LockscreenSourceKind sourceKind);
    StorageFile? CurrentImage { get; }
    LockscreenSource? CurrentSource { get; }
    BitmapImage? CurrentImageBitmap { get; }
}
