using LockscreenGif.CustomControls;
using LockscreenGif.Models;
using LockscreenGif.Services;
using LockscreenGif.Services.Analytics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.System;

namespace LockscreenGif.Views;

public sealed partial class MainPage
{
    private VideoFrameIndex? _frames;
    private int _startFrame,
        _endFrame,
        _previewFrame;
    private bool _mediaReady,
        _playing,
        _interacting,
        _resumeAfterInteraction;
    private bool _playbackRendering,
        _playbackSeekPending;
    private bool _previewErrorReported,
        _thumbnailErrorReported;
    private string? _startError,
        _endError;
    private bool _startInvalid => _startError is not null;
    private bool _endInvalid => _endError is not null;
    private bool _scrubbingPlayhead;
    private CancellationTokenSource? _videoLoadCts,
        _editorCts,
        _previewCts;
    private readonly Dictionary<int, byte[]> _previewCache = [];
    private double _startSec => _frames?.TimeAt(_startFrame) ?? 0;
    private double _endSec => _frames?.TimeAt(_endFrame) ?? 0;
    private bool CanInteractWithEditor => IsLoaded && Flow.Stage == MainFlowStage.Edit && !Flow.IsBusy;

    private void TrimControlsPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Reflow using the editor's available width, including display scaling and
        // the page's surrounding margins, rather than the outer window width.
        var narrow = e.NewSize.Width < 760;
        Grid.SetColumnSpan(StartBoundaryCard, narrow ? 3 : 1);
        Grid.SetColumn(EndBoundaryCard, narrow ? 0 : 2);
        Grid.SetColumnSpan(EndBoundaryCard, narrow ? 3 : 1);
        Grid.SetRow(EndBoundaryCard, narrow ? 1 : 0);
        EndBoundaryCard.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
    }

    private void SetPreviewStatus(string? message)
    {
        if (message is null && _mediaReady && VideoPreview.MediaPlayer is null)
        {
            message = "Playback unavailable. You can still preview frames, trim the clip and generate a GIF.";
        }
        PreviewStatus.Text = message ?? "";
        PreviewStatus.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateEditor(bool resetInput = true, TimelinePart? boundary = null)
    {
        if (_frames is null)
        {
            return;
        }

        if (resetInput)
        {
            _updatingTimeText = true;
            if (boundary is null or TimelinePart.Start)
            {
                _startError = null;
                StartTimeTextBox.Text = VideoFrameIndex.FormatTime(_startSec);
            }
            if (boundary is null or TimelinePart.End)
            {
                _endError = null;
                EndTimeTextBox.Text = VideoFrameIndex.FormatTime(_endSec);
            }
            UpdateValidationText();
            _updatingTimeText = false;
        }
        SelectionSummary.Text =
            $"Duration {VideoFrameIndex.FormatTime(_endSec - _startSec)} · {_endFrame - _startFrame:N0} frames selected · {_frames.Count:N0} total";
        StartFrameLabel.Text = $"First included frame: {_startFrame + 1:N0}";
        EndFrameLabel.Text = $"Last included frame: {_endFrame:N0}";
        StartBackButton.IsEnabled = _startFrame > 0;
        StartForwardButton.IsEnabled = _startFrame + 1 < _endFrame;
        EndBackButton.IsEnabled = _endFrame > _startFrame + 1;
        EndForwardButton.IsEnabled = _endFrame < _frames.Count;
        UpdatePreviewPosition();
        UpdateGenerateEnabled();
        UpdateFileSizeWarning();
    }

    private void UpdateValidationText()
    {
        TrimError.Text = _startError ?? _endError ?? "";
        TrimError.Visibility = _startInvalid || _endInvalid ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateGenerateEnabled() => SyncDraftState();

    private void UpdatePreviewPosition()
    {
        if (_frames is null)
        {
            return;
        }

        TrimTimeline.SetFrames(_startFrame, _endFrame, _previewFrame);
    }

    private bool TryReadTime(TextBox box, out int frame, out string? error)
    {
        frame = 0;
        error = null;
        if (_frames is null)
        {
            return false;
        }

        var start = ReferenceEquals(box, StartTimeTextBox);
        if (!VideoFrameIndex.TryParseTime(box.Text, out var seconds))
        {
            error = "Enter seconds (5.25), minutes:seconds (1:05.25), or hours:minutes:seconds.";
        }
        else if (seconds > _frames.Duration + 0.00051)
        {
            error = $"Time must be within the video (0–{VideoFrameIndex.FormatTime(_frames.Duration)}).";
        }
        else
        {
            frame = _frames.NearestBoundary(Math.Min(seconds, _frames.Duration));
            if (start ? frame >= _endFrame : frame <= _startFrame)
            {
                error = "End must be after start. Select at least one frame.";
            }
        }
        return error is null;
    }

    private bool CommitTime(TextBox box)
    {
        if (_frames is null || !CanInteractWithEditor)
        {
            return false;
        }
        var start = ReferenceEquals(box, StartTimeTextBox);
        TryReadTime(box, out var frame, out var error);
        if (start)
        {
            _startError = error is null ? null : "Start: " + error;
        }
        else
        {
            _endError = error is null ? null : "End: " + error;
        }

        UpdateValidationText();
        if (error is not null)
        {
            UpdateGenerateEnabled();
            return false;
        }
        var changed = start ? frame != _startFrame : frame != _endFrame;
        if (start)
        {
            _startFrame = frame;
        }
        else
        {
            _endFrame = frame;
        }

        _updatingTimeText = true;
        box.Text = VideoFrameIndex.FormatTime(start ? _startSec : _endSec);
        _updatingTimeText = false;
        UpdateValidationText();
        UpdateEditor(resetInput: false);
        if (changed)
        {
            ShowFrame(start ? _startFrame : _endFrame - 1);
        }

        return true;
    }

    private void TimeTextBox_LostFocus(object sender, RoutedEventArgs e) => CommitTime((TextBox)sender);

    private void TimeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_flowReady || _updatingTimeText || Flow.IsBusy || _frames is null)
        {
            return;
        }
        TryReadTime(StartTimeTextBox, out _, out var startError);
        TryReadTime(EndTimeTextBox, out _, out var endError);
        _startError = startError is null ? null : "Start: " + startError;
        _endError = endError is null ? null : "End: " + endError;
        UpdateValidationText();
        SyncDraftState();
    }

    private void TimeTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var box = (TextBox)sender;
        if (e.Key == VirtualKey.Enter)
        {
            CommitTime(box);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            _updatingTimeText = true;
            if (ReferenceEquals(box, StartTimeTextBox))
            {
                _startError = null;
                box.Text = VideoFrameIndex.FormatTime(_startSec);
            }
            else
            {
                _endError = null;
                box.Text = VideoFrameIndex.FormatTime(_endSec);
            }
            _updatingTimeText = false;
            UpdateValidationText();
            UpdateGenerateEnabled();
            e.Handled = true;
        }
    }

    private void NudgeBoundary_Click(object sender, RoutedEventArgs e)
    {
        if (_frames is null || !CanInteractWithEditor)
        {
            return;
        }

        var tag = (string)((FrameworkElement)sender).Tag;
        var start = tag.StartsWith("Start", StringComparison.Ordinal);
        var delta = tag.EndsWith("Back", StringComparison.Ordinal) ? -1 : 1;
        if (start)
        {
            _startFrame = Math.Clamp(_startFrame + delta, 0, _endFrame - 1);
        }
        else
        {
            _endFrame = Math.Clamp(_endFrame + delta, _startFrame + 1, _frames.Count);
        }

        UpdateEditor(boundary: start ? TimelinePart.Start : TimelinePart.End);
        ShowFrame(start ? _startFrame : _endFrame - 1);
    }

    private void StartHere_Click(object sender, RoutedEventArgs e)
    {
        if (_frames is null || !CanInteractWithEditor)
        {
            return;
        }

        if (_previewFrame >= _endFrame)
        {
            ShowTrimError("Move the preview before the clip end to set its start.");
            return;
        }
        _startFrame = _previewFrame;
        UpdateEditor(boundary: TimelinePart.Start);
    }

    private void EndHere_Click(object sender, RoutedEventArgs e)
    {
        if (_frames is null || !CanInteractWithEditor)
        {
            return;
        }

        if (_previewFrame < _startFrame)
        {
            ShowTrimError("Move the preview to or after the clip start to set its end.");
            return;
        }
        _endFrame = _previewFrame + 1;
        UpdateEditor(boundary: TimelinePart.End);
    }

    private void ShowTrimError(string message)
    {
        TrimError.Text = message;
        TrimError.Visibility = Visibility.Visible;
    }

    private void ResetTrim_Click(object sender, RoutedEventArgs e)
    {
        if (_frames is null || !CanInteractWithEditor)
        {
            return;
        }

        _startFrame = 0;
        _endFrame = _frames.Count;
        UpdateEditor();
        ShowFrame(0);
    }

    private void Timeline_InteractionStarted(object? sender, EventArgs e)
    {
        if (!CanInteractWithEditor)
        {
            return;
        }
        _resumeAfterInteraction = _playing;
        _scrubbingPlayhead = false;
        PausePreview();
        _interacting = true;
    }

    private void Timeline_FrameChanged(object? sender, TimelineChangedEventArgs e)
    {
        if (_frames is null || !CanInteractWithEditor)
        {
            return;
        }

        _scrubbingPlayhead = e.Part == TimelinePart.Playhead;
        switch (e.Part)
        {
            case TimelinePart.Start:
                _startFrame = e.Frame;
                _previewFrame = e.Frame;
                break;
            case TimelinePart.End:
                _endFrame = e.Frame;
                _previewFrame = e.Frame - 1;
                break;
            default:
                _previewFrame = e.Frame;
                break;
        }
        PreviewStill.Visibility = Visibility.Collapsed;
        Seek(_frames.TimeAt(_previewFrame));
        UpdateEditor(resetInput: !_scrubbingPlayhead, boundary: e.Part);
    }

    private void Timeline_InteractionCompleted(object? sender, EventArgs e)
    {
        _interacting = false;
        if (!CanInteractWithEditor)
        {
            _resumeAfterInteraction = false;
            return;
        }
        if (_resumeAfterInteraction)
        {
            StartPlayback(fromStart: !_scrubbingPlayhead);
        }
        else
        {
            ShowFrame(_previewFrame);
        }
    }

    private void SkipToSelectionBoundary_Click(object sender, RoutedEventArgs e)
    {
        if (_playing || _frames is null || !CanInteractWithEditor)
        {
            return;
        }

        ShowFrame(ReferenceEquals(sender, SelectionStartButton) ? _startFrame : _endFrame - 1);
    }

    private void PlaySelection_Click(object sender, RoutedEventArgs e)
    {
        if (!CanInteractWithEditor)
        {
            return;
        }
        if (_playing)
        {
            PausePreview();
            ShowFrame(_previewFrame);
        }
        else
        {
            StartPlayback();
        }
    }

    private void StartPlayback(bool fromStart = true)
    {
        if (_frames is null || !_mediaReady || !CanInteractWithEditor || VideoPreview.MediaPlayer is null)
        {
            return;
        }

        _previewCts?.Cancel();
        PreviewStill.Visibility = Visibility.Collapsed;
        SetPreviewStatus(null);
        SetPreviewLoading(false);
        if (fromStart || _previewFrame < _startFrame || _previewFrame >= _endFrame)
        {
            _previewFrame = _startFrame;
        }

        SeekForPlayback(_frames.TimeAt(_previewFrame));
        _playing = true;
        UpdatePreviewPosition();
        UpdatePlaybackButtons();
        VideoPreview.MediaPlayer.Play();
        StartPlaybackRendering();
    }

    private async void ShowFrame(int frame)
    {
        if (_frames is null || _videoFile is null || !_mediaReady)
        {
            return;
        }

        PausePreview();
        var index = _frames;
        _previewFrame = Math.Clamp(frame, 0, index.Count - 1);
        frame = _previewFrame;
        Seek(index.TimeAt(frame));
        UpdatePreviewPosition();
        _previewCts?.Dispose();
        _previewCts = CancellationTokenSource.CreateLinkedTokenSource(_editorCts?.Token ?? CancellationToken.None);
        var request = _previewCts;
        var token = request.Token;
        var input = _videoFile.Path;
        PreviewStill.Visibility = Visibility.Collapsed;
        SetPreviewStatus(null);
        try
        {
            // Debounce held buttons and rapid edits. Verify the exact indexed frames
            // after seeking, rather than decoding the video from its beginning again.
            if (!_previewCache.TryGetValue(frame, out var bytes))
            {
                SetPreviewLoading(true, frame);
                await Task.Delay(100, token);
                var first = Math.Max(0, frame - 8);
                var last = Math.Min(index.Count, frame + 9);
                var images = await VideoFrameService.PreviewWindowAsync(input, index, first, last, token);
                token.ThrowIfCancellationRequested();
                for (var i = 0; i < images.Count; i++)
                {
                    if (_previewCache.Count >= 32)
                    {
                        _previewCache.Remove(_previewCache.Keys.First());
                    }

                    _previewCache[first + i] = images[i];
                }
                bytes = images[frame - first];
            }
            var bitmap = await BitmapAsync(bytes);
            token.ThrowIfCancellationRequested();
            PreviewStill.Source = bitmap;
            PreviewStill.Visibility = Visibility.Visible;
            SetPreviewStatus(null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            CapturePreviewError(ex);
            SetPreviewStatus("Exact frame preview could not load. Move a frame to retry.");
            Logger.Error("Frame preview failed", ex);
        }
        finally
        {
            // A superseded request must not hide the newer request's indicator.
            if (ReferenceEquals(_previewCts, request))
            {
                SetPreviewLoading(false);
            }
        }
    }

    private static async Task<BitmapImage> BitmapAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private async Task LoadThumbnailsAsync(string input, VideoFrameIndex index, CancellationToken token)
    {
        try
        {
            var bytes = await VideoFrameService.ThumbnailsAsync(input, index, token);
            var images = new List<BitmapImage>();
            foreach (var image in bytes)
            {
                token.ThrowIfCancellationRequested();
                images.Add(await BitmapAsync(image));
            }
            token.ThrowIfCancellationRequested();
            TrimTimeline.SetThumbnails(index, images);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && ReferenceEquals(index, _frames) && !_thumbnailErrorReported)
            {
                _thumbnailErrorReported = true;
                _analyticsService.CaptureException(ex, AnalyticsErrorContext.VideoThumbnails);
            }
            Logger.Error("Timeline thumbnails failed", ex);
        }
        finally
        {
            TrimTimeline.CompleteThumbnailLoading(index);
        }
    }

    private void StartPlaybackRendering()
    {
        if (_playbackRendering)
        {
            return;
        }

        _playbackRendering = true;
        CompositionTarget.Rendering += Playback_Rendering;
    }

    private void StopPlaybackRendering()
    {
        if (!_playbackRendering)
        {
            return;
        }

        CompositionTarget.Rendering -= Playback_Rendering;
        _playbackRendering = false;
    }

    private void Playback_Rendering(object? sender, object args)
    {
        if (!_playing || _interacting || _frames is null || _session is null)
        {
            return;
        }
        // Sample the player's actual clock each UI frame. PositionChanged notifications
        // are too infrequent for animation; a wall clock would drift while buffering.
        if (_session.PlaybackState != MediaPlaybackState.Playing)
        {
            return;
        }

        var seconds = _session.Position.TotalSeconds;
        if (_playbackSeekPending)
        {
            // Seeking is asynchronous. Do not repeatedly issue a loop seek while the
            // player still reports the old position outside the selected interval.
            if (seconds < _startSec || seconds >= _endSec)
            {
                return;
            }

            _playbackSeekPending = false;
        }
        if (seconds >= _endSec)
        {
            SeekPlaybackStart();
            return;
        }
        if (seconds < _startSec)
        {
            SeekPlaybackStart();
            return;
        }

        _previewFrame = _frames.FrameAt(seconds);
        TrimTimeline.SetPlaybackPosition(seconds, _previewFrame);
    }

    private void SeekPlaybackStart()
    {
        SeekForPlayback(_startSec);
        _previewFrame = _startFrame;
        UpdatePreviewPosition();
    }

    private void SeekForPlayback(double seconds)
    {
        if (_session is null)
        {
            return;
        }
        // A no-op seek need not raise SeekCompleted.
        _playbackSeekPending = _session.Position != TimeSpan.FromSeconds(seconds);
        Seek(seconds);
    }

    private void Session_SeekCompleted(MediaPlaybackSession sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            // A very short clip can pass its end between render samples. Completion
            // must release the guard even if no sample fell inside the selected range.
            if (ReferenceEquals(sender, _session))
            {
                _playbackSeekPending = false;
            }
        });

    private void VideoPreview_MediaEnded(MediaPlayer sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, VideoPreview.MediaPlayer) || !_playing)
            {
                return;
            }

            StartPlayback();
        });

    private void VideoPreview_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, VideoPreview.MediaPlayer))
            {
                return;
            }

            var error = args.ExtendedErrorCode ?? new InvalidDataException("The video preview failed.");
            _analyticsService.CaptureException(
                error,
                AnalyticsErrorContext.VideoPreview,
                new AnalyticsProperties { MediaLoadStage = AnalyticsMediaLoadStage.PlayingPreview, PlaybackAvailable = false }
            );
            Logger.Error("Windows video playback failed; keeping frame editing and conversion", error);
            PausePreview();
            ReleasePreviewPlayer(preserveEditor: true);
            UpdateGenerateEnabled();
            ShowPlaybackUnavailable(error);
            if (CanInteractWithEditor)
            {
                ShowFrame(_previewFrame);
            }
        });

    private void ShowPlaybackUnavailable(Exception exception)
    {
        OperationStatus.Title = "Video playback is unavailable";
        OperationStatus.Message =
            (
                AnalyticsProperties.ClassifyError(exception) == AnalyticsErrorKind.CodecMissing
                    ? "Windows could not find a compatible playback codec."
                    : MediaFailureGuidance.Message(exception, "Windows could not play this video.")
            ) + " You can still preview individual frames, trim the clip and generate your GIF.";
        OperationStatus.Severity = InfoBarSeverity.Warning;
        OperationStatus.IsOpen = true;
    }

    private void CapturePreviewError(Exception exception)
    {
        // Preview failures can recur while scrubbing or playing. Report only the
        // first failure for the current video, then allow a new selection to retry.
        if (_previewErrorReported)
        {
            return;
        }

        _previewErrorReported = true;
        _analyticsService.CaptureException(exception, AnalyticsErrorContext.VideoPreview);
    }
}
