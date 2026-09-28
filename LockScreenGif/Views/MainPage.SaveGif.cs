using System.Diagnostics;
using LockscreenGif.Models;
using LockscreenGif.Services;
using LockscreenGif.Services.Analytics;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
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
        await RunActionAsync(
            async token =>
            {
                var source = _generatedGif;
                if (source is null)
                {
                    return;
                }

                Stopwatch? timer = null;
                try
                {
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
                        _analyticsService.Track(
                            AnalyticsEvent.GifSaveCompleted,
                            new AnalyticsProperties { Outcome = AnalyticsOutcome.Cancelled }
                        );
                        return;
                    }

                    timer = Stopwatch.StartNew();
                    await PickedFileWriter.CopyAsync(source, destination);

                    OperationStatus.Title = "GIF saved";
                    OperationStatus.Message = "You can select the saved copy with Choose GIF whenever you want to use it again.";
                    OperationStatus.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;
                    OperationStatus.IsOpen = true;
                    _analyticsService.Track(
                        AnalyticsEvent.GifSaveCompleted,
                        new AnalyticsProperties { Outcome = AnalyticsOutcome.Succeeded, DurationMs = timer.Elapsed.TotalMilliseconds }
                    );
                }
                catch (Exception ex)
                {
                    _analyticsService.TrackFailure(
                        AnalyticsEvent.GifSaveCompleted,
                        ex,
                        new AnalyticsProperties { DurationMs = timer?.Elapsed.TotalMilliseconds }
                    );
                    throw;
                }
            },
            MainFlowOperation.Saving
        );
}
