using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace LockscreenGif.Contracts.Services;

public interface ILockscreenService
{
    public Task<bool> ApplyGifAsLockscreenAsync();
    public StorageFile? CurrentImage
    {
        get; set;
    }
    public BitmapImage? CurrentImageBitmap
    {
        get;
    }
}