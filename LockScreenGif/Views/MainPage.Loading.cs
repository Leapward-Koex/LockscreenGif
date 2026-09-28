using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.System;

namespace LockscreenGif.Views;

public sealed partial class MainPage
{
    private bool _isVideoLoading;

    private void BeginVideoLoading(string fileName)
    {
        _isVideoLoading = true;
        PageContent.IsEnabled = false;
        VideoLoadFileName.Text = fileName;
        VideoLoadStatus.Text = "Reading video details…";
        CancelVideoLoadButton.Content = "Cancel";
        CancelVideoLoadButton.IsEnabled = true;
        VideoLoadPanel.Visibility = Visibility.Visible;
        VideoLoadRing.IsActive = true;
        CancelVideoLoadButton.Focus(FocusState.Programmatic);
    }

    private void EndVideoLoading()
    {
        _isVideoLoading = false;
        VideoLoadRing.IsActive = false;
        VideoLoadPanel.Visibility = Visibility.Collapsed;
        PageContent.IsEnabled = true;
        if (IsLoaded)
        {
            RefreshFlowUi();
            FocusStage();
        }
    }

    private async void CancelVideoLoad_Click(object sender, RoutedEventArgs e) => await CancelVideoLoadingAsync();

    private async void VideoLoadPanel_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        e.Handled = true;
        await CancelVideoLoadingAsync();
    }

    private async Task CancelVideoLoadingAsync()
    {
        if (!_isVideoLoading || _videoLoadCts is not { IsCancellationRequested: false } source)
        {
            return;
        }

        CancelVideoLoadButton.IsEnabled = false;
        CancelVideoLoadButton.Content = "Cancelling…";
        VideoLoadStatus.Text = "Stopping frame loading…";
        // Process cancellation callbacks must not delay rendering the cancelling state.
        await source.CancelAsync();
    }

    private void SetPreviewLoading(bool loading, int frame = 0)
    {
        PreviewLoadingRing.IsActive = loading;
        PreviewLoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        if (loading)
        {
            PreviewLoadingText.Text = $"Loading frame {frame + 1:N0}…";
        }
    }

    private static Task<MediaPlayer> OpenPreviewPlayerAsync(StorageFile file, CancellationToken token) =>
        Task.Run(
            async () =>
            {
                token.ThrowIfCancellationRequested();
                var player = new MediaPlayer { IsMuted = true, AutoPlay = false };
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void Opened(MediaPlayer sender, object args) => ready.TrySetResult(true);
                void Failed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
                    ready.TrySetException(args.ExtendedErrorCode ?? new InvalidDataException("The video preview could not be opened."));
                var opened = false;
                player.MediaOpened += Opened;
                player.MediaFailed += Failed;
                try
                {
                    player.Source = MediaSource.CreateFromStorageFile(file);
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                    opened = true;
                    return player;
                }
                finally
                {
                    player.MediaOpened -= Opened;
                    player.MediaFailed -= Failed;
                    if (!opened)
                    {
                        player.Dispose();
                    }
                }
            },
            token
        );
}
