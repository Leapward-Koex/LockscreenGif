using LockscreenGif.Models;
using LockscreenGif.Services;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace LockscreenGif.Contracts.Services;

public interface ILockscreenService
{
    Task<bool> ApplyGifAsLockscreenAsync();
    Task<LockscreenApplyResult> ApplyAsync(
        string sourcePath,
        bool useWindowsApi,
        Action<LockscreenApplyEvent>? progress = null,
        CancellationToken cancellationToken = default,
        LockscreenGif.Services.Lockscreen.ICachePermissionSession? permissionSession = null
    );
    Task<DeleteFilesResult?> RemoveAppliedGif();
    Task WaitForIdleAsync();
    bool IsApplying { get; }
    string CacheDirectory { get; }
    StorageFile? CurrentImage { get; set; }
    BitmapImage? CurrentImageBitmap { get; }
}
