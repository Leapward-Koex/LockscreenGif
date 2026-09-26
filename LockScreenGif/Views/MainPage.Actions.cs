using LockscreenGif.Helpers;
using LockscreenGif.Services;
using LockscreenGif.Services.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LockscreenGif.Views;

public sealed partial class MainPage
{
    private bool _actionPending;

    private async Task RunActionAsync(Func<Task> action, bool changesLockscreen = false)
    {
        if (_actionPending)
        {
            return;
        }

        if (changesLockscreen && App.GetService<DiagnosticsSessionService>().IsRunning)
        {
            OperationStatus.Title = "A diagnostic test is running";
            OperationStatus.Message = "Finish or stop the test on the Diagnostics page before changing the lock screen.";
            OperationStatus.IsOpen = true;
            return;
        }
        _actionPending = true;
        OperationStatus.IsOpen = false;
        ApplyButton.IsEnabled = false;
        SaveGeneratedGifButton.IsEnabled = false;
        removeAnimatedLockscreen.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Logger.Error($"Main page action failed: {ex.GetType().Name} (0x{ex.HResult:X8})", ex);
            OperationStatus.Title = "The action could not be completed";
            OperationStatus.Message = $"{ex.GetType().Name}: {ex.Message}";
            OperationStatus.IsOpen = true;
        }
        finally
        {
            _actionPending = false;
            SaveGeneratedGifButton.IsEnabled = true;
            ApplyButton.IsEnabled = _lockscreenService.CurrentImage is not null;
            removeAnimatedLockscreen.IsEnabled = true;
            UpdateGenerateEnabled();
        }
    }

    private async void SetLockscreenButton_click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            async () =>
            {
                Logger.Info("Trying to set lockscreen");
                var success = await _lockscreenService.ApplyGifAsLockscreenAsync();
                // Keep the source available for retries and diagnostics until next startup.
                var key = success ? "AppNotificationSuccess" : "AppNotificationFailure";
                _notificationService.Show(string.Format(key.GetLocalized(), AppContext.BaseDirectory));
            },
            changesLockscreen: true
        );

    private async void RemoveAnimatedLockscreenButton_click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            async () =>
            {
                var result = await _lockscreenService.RemoveAppliedGif();
                if (result is null)
                {
                    _notificationService.Show(string.Format("AppNotificationDeleteFailure".GetLocalized(), AppContext.BaseDirectory));
                }
                else if (result.FailedDeletions != 0)
                {
                    _notificationService.Show(
                        string.Format(
                            "AppNotificationDeletePartialFailure".GetLocalized(),
                            AppContext.BaseDirectory,
                            result.SuccessfulDeletions,
                            result.FailedDeletions
                        )
                    );
                }
                else
                {
                    _notificationService.Show(string.Format("AppNotificationDeleteSuccess".GetLocalized(), AppContext.BaseDirectory));
                    OperationStatus.Title = "Animated lock-screen variants removed";
                    OperationStatus.Message = "You may need to lock and unlock before applying another GIF.";
                    OperationStatus.IsOpen = true;
                }
            },
            changesLockscreen: true
        );

    private static async Task<StorageFile?> PickFileAsync(PickerLocationId location, params string[] extensions)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = location };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        foreach (var extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        return await picker.PickSingleFileAsync();
    }

    private async void OpenGifButton_click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(async () =>
        {
            var file = await PickFileAsync(PickerLocationId.PicturesLibrary, ".gif");
            if (file is null)
            {
                return;
            }

            ClearGeneratedGif();
            _lockscreenService.CurrentImage = file;
            currentImage.Source = _lockscreenService.CurrentImageBitmap!;
        });

    private async void OpenVideoButton_click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(async () =>
        {
            var file = await PickFileAsync(PickerLocationId.VideosLibrary, ".mp4", ".mkv");
            if (file is null)
            {
                return;
            }

            PausePreview();
            GenerateButton.IsEnabled = false;
            _videoLoadCts?.Dispose();
            var loadSource = _videoLoadCts = new CancellationTokenSource();
            var token = loadSource.Token;
            BeginVideoLoading(file.Name);
            MediaPlayer? preparedPlayer = null;
            var scanning = false;
            try
            {
                var (fps, width, height) = await Task.Run(() => GetVideoInfoAsync(file), token).WaitAsync(token);
                if (fps is null || !double.IsFinite(fps.Value) || fps <= 0 || width == 0 || height == 0)
                {
                    throw new InvalidDataException("This video has no usable video stream.");
                }

                token.ThrowIfCancellationRequested();
                scanning = true;
                VideoLoadStatus.Text = "Reading video frames…";
                var progress = new Progress<int>(count =>
                {
                    if (_isVideoLoading && scanning && ReferenceEquals(_videoLoadCts, loadSource) && !token.IsCancellationRequested)
                    {
                        VideoLoadStatus.Text = $"Reading video frames… {count:N0} found";
                    }
                });
                var frames = await VideoFrameService.IndexAsync(file.Path, fps.Value, progress, token);
                scanning = false;
                token.ThrowIfCancellationRequested();
                VideoLoadStatus.Text = $"Read {frames.Count:N0} frames. Opening preview…";
                preparedPlayer = await OpenPreviewPlayerAsync(file, token);
                token.ThrowIfCancellationRequested();

                // Keep the previous selection intact until both scanning and media opening succeed.
                _previewCts?.Cancel();
                _editorCts?.Cancel();
                _editorCts?.Dispose();
                _editorCts = new CancellationTokenSource();
                _previewCache.Clear();
                PreviewStill.Source = null;
                PreviewStill.Visibility = Visibility.Collapsed;
                ClearGeneratedGif();
                _videoFile = file;
                _frames = frames;
                _startFrame = _previewFrame = 0;
                _endFrame = frames.Count;
                _videoFps = fps;
                _videoWidth = width;
                _videoHeight = height;
                PopulateResolutionList();
                PopulateFpsList();
                TrimTimeline.Configure(frames);
                GenerateLoading.Visibility = Visibility.Collapsed;
                if (_session is not null)
                {
                    _session.SeekCompleted -= Session_SeekCompleted;
                }

                if (VideoPreview.MediaPlayer is { } oldPlayer)
                {
                    oldPlayer.MediaEnded -= VideoPreview_MediaEnded;
                    oldPlayer.MediaFailed -= VideoPreview_MediaFailed;
                    oldPlayer.Dispose();
                }
                VideoPreview.SetMediaPlayer(preparedPlayer);
                preparedPlayer = null; // The page now owns this player.
                VideoPreview.MediaPlayer.MediaEnded += VideoPreview_MediaEnded;
                VideoPreview.MediaPlayer.MediaFailed += VideoPreview_MediaFailed;
                _session = VideoPreview.MediaPlayer.PlaybackSession;
                _session.SeekCompleted += Session_SeekCompleted;
                _mediaReady = true;
                ShowVideoUi();
                UpdateEditor();
                ShowFrame(0);
                _ = LoadThumbnailsAsync(file.Path, frames, _editorCts.Token);
            }
            catch (OperationCanceledException)
            {
                OperationStatus.Title = "Video loading cancelled";
                OperationStatus.Message = _videoFile is null
                    ? "Choose a video when you're ready."
                    : "Your previous selection has been kept.";
                OperationStatus.IsOpen = true;
            }
            finally
            {
                scanning = false;
                preparedPlayer?.Dispose();
                EndVideoLoading();
            }
        });
}
