using Microsoft.UI.Xaml;

namespace LockscreenGif.Views;

public sealed partial class MainPage
{
    private void MainPage_Unloaded(object sender, RoutedEventArgs args)
    {
        StopLockscreenModePolling();
        SuspendEditorPlayback();
        _videoLoadCts?.Cancel();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        App.MainWindow.Closed -= MainWindow_Closed;
        StopLockscreenModePolling();
        _playing = false;
        StopPlaybackRendering();
        _videoLoadCts?.Cancel();
        _previewCts?.Cancel();
        _editorCts?.Cancel();
        ReleasePreviewPlayer();
    }

    private void ReleasePreviewPlayer(bool preserveEditor = false)
    {
        _mediaReady = preserveEditor && _mediaReady;
        _playbackSeekPending = false;
        var session = _session;
        _session = null;
        if (session is not null)
        {
            session.SeekCompleted -= Session_SeekCompleted;
        }

        if (VideoPreview.MediaPlayer is { } player)
        {
            player.MediaEnded -= VideoPreview_MediaEnded;
            player.MediaFailed -= VideoPreview_MediaFailed;
            // Closed can precede Unloaded. Clear the control's reference before
            // disposal so unloading and queued callbacks cannot use a closed player.
            VideoPreview.SetMediaPlayer(null);
            player.Dispose();
        }
        UpdatePlaybackButtons();
    }

    private void SuspendEditorPlayback()
    {
        _resumeAfterInteraction = false;
        _interacting = false;
        PausePreview();
    }

    private void PausePreview()
    {
        _playing = false;
        StopPlaybackRendering();
        _playbackSeekPending = false;
        _previewCts?.Cancel();
        VideoPreview.MediaPlayer?.Pause();
        UpdatePlaybackButtons();
        SetPreviewLoading(false);
    }

    private void UpdatePlaybackButtons()
    {
        PlaySelectionButton.Content = _playing ? "Pause" : "Play";
        PlaySelectionButton.IsEnabled = VideoPreview.MediaPlayer is not null;
        SelectionStartButton.IsEnabled = !_playing;
        SelectionEndButton.IsEnabled = !_playing;
    }
}
