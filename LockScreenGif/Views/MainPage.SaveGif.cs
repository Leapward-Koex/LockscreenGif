using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Provider;
using WinRT.Interop;

namespace LockscreenGif.Views;

public sealed partial class MainPage
{
    private StorageFile? _generatedGif;
    private string _generatedGifName = "Lockscreen";

    private void ClearGeneratedGif()
    {
        _generatedGif = null;
        SaveGeneratedGifPanel.Visibility = Visibility.Collapsed;
    }

    private async void SaveGeneratedGifButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(async () =>
        {
            var source = _generatedGif;
            if (source is null)
            {
                return;
            }

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                SuggestedFileName = _generatedGifName,
                DefaultFileExtension = ".gif",
            };
            picker.FileTypeChoices.Add("GIF image", new List<string> { ".gif" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var destination = await picker.PickSaveFileAsync();
            if (destination is null)
            {
                return;
            }

            if (!string.Equals(source.Path, destination.Path, StringComparison.OrdinalIgnoreCase))
            {
                CachedFileManager.DeferUpdates(destination);
                try
                {
                    await source.CopyAndReplaceAsync(destination);
                }
                finally
                {
                    var status = await CachedFileManager.CompleteUpdatesAsync(destination);
                    if (status != FileUpdateStatus.Complete && status != FileUpdateStatus.CompleteAndRenamed)
                    {
                        throw new IOException($"The selected location could not finish saving the GIF ({status}).");
                    }
                }
            }

            OperationStatus.Title = "GIF saved";
            OperationStatus.Message = "You can select the saved copy with Browse GIF whenever you want to use it again.";
            OperationStatus.IsOpen = true;
        });
}
