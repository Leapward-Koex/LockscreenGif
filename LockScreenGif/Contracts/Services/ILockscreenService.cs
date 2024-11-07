using LockscreenGif.Services;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace LockscreenGif.Contracts.Services;

public interface ILockscreenService
{
    public Task<bool> ApplyGifAsLockscreenAsync();
    public Task<DeleteFilesResult?> RemoveAppliedGif();
    public StorageFile? CurrentImage
    {
        get; set;
    }
    public BitmapImage? CurrentImageBitmap
    {
        get;
    }
}