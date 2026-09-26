using LockscreenGif.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace LockscreenGif.CustomControls;

public enum TimelinePart
{
    Start,
    End,
    Playhead,
}

public sealed class TimelineChangedEventArgs(TimelinePart part, int frame) : EventArgs
{
    public TimelinePart Part { get; } = part;
    public int Frame { get; } = frame;
}

public sealed partial class VideoTimeline : UserControl
{
    // Keep the complete hit targets inside the control at the source endpoints.
    private const double TrackInset = 32;
    private const double TrimHitWidth = 32;
    private const double TrimCapWidth = 12;
    private const double PlayheadWidth = 32;
    private VideoFrameIndex? _index;
    private int _start,
        _end,
        _position;
    private double _dragX,
        _positionSeconds;
    private readonly TranslateTransform _playheadTransform = new();
    private readonly TranslateTransform _playheadLineTransform = new();
    private readonly TranslateTransform _playheadShadowTransform = new();
    public event EventHandler? InteractionStarted;
    public event EventHandler? InteractionCompleted;
    public event EventHandler<TimelineChangedEventArgs>? FrameChanged;

    public VideoTimeline()
    {
        InitializeComponent();
        PlayheadHandle.RenderTransform = _playheadTransform;
        PlayheadLine.RenderTransform = _playheadLineTransform;
        PlayheadShadow.RenderTransform = _playheadShadowTransform;
        Wire(StartHandle, TimelinePart.Start);
        Wire(EndHandle, TimelinePart.End);
        Wire(PlayheadHandle, TimelinePart.Playhead);
    }

    public void Configure(VideoFrameIndex index)
    {
        _index = index;
        Filmstrip.Children.Clear();
        Filmstrip.ColumnDefinitions.Clear();
        SetFrames(0, index.Count, 0);
    }

    public void SetThumbnails(IEnumerable<ImageSource> sources)
    {
        Filmstrip.Children.Clear();
        Filmstrip.ColumnDefinitions.Clear();
        foreach (var source in sources)
        {
            Filmstrip.ColumnDefinitions.Add(new ColumnDefinition());
            var image = new Image { Source = source, Stretch = Stretch.UniformToFill };
            Grid.SetColumn(image, Filmstrip.Children.Count);
            Filmstrip.Children.Add(image);
        }
    }

    public void SetFrames(int start, int end, int position)
    {
        _start = start;
        _end = end;
        _position = position;
        _positionSeconds = _index?.TimeAt(position) ?? 0;
        StartHandle.MinimumFrame = 0;
        StartHandle.MaximumFrame = end - 1;
        EndHandle.MinimumFrame = start + 1;
        EndHandle.MaximumFrame = _index?.Count ?? 1;
        PlayheadHandle.MinimumFrame = 0;
        PlayheadHandle.MaximumFrame = (_index?.Count ?? 1) - 1;
        StartHandle.UpdateFrame(start);
        EndHandle.UpdateFrame(end);
        PlayheadHandle.UpdateFrame(position);
        if (_index is not null)
        {
            AutomationProperties.SetHelpText(
                StartHandle,
                $"First included frame {start + 1}, {VideoFrameIndex.FormatTime(_index.TimeAt(start))}"
            );
            AutomationProperties.SetHelpText(
                EndHandle,
                $"Last included frame {end}, ends at {VideoFrameIndex.FormatTime(_index.TimeAt(end))}"
            );
            AutomationProperties.SetHelpText(PlayheadHandle, $"Frame {position + 1} of {_index.Count}");
        }
        Render();
    }

    public void SetPlaybackPosition(double seconds, int frame)
    {
        if (_index is null)
        {
            return;
        }

        _positionSeconds = Math.Clamp(seconds, 0, _index.Duration);
        if (_position != frame)
        {
            _position = frame;
            PlayheadHandle.UpdateFrame(frame);
            AutomationProperties.SetHelpText(PlayheadHandle, $"Frame {frame + 1} of {_index.Count}");
        }
        RenderPlayhead();
    }

    private void RenderPlayhead()
    {
        if (_index is null)
        {
            return;
        }

        var position = TrackInset + _positionSeconds / _index.Duration * TrackWidth;
        // Render transforms preserve fractional pixels and avoid laying out the
        // selection and thumbnails on every playback update.
        _playheadTransform.X = position - PlayheadWidth / 2;
        _playheadShadowTransform.X = position - 2;
        _playheadLineTransform.X = position - 1;
    }

    private void Wire(FrameHandle handle, TimelinePart part)
    {
        handle.DragStarted += (_, _) =>
        {
            handle.Focus(FocusState.Pointer);
            _dragX = X(handle.Frame);
            InteractionStarted?.Invoke(this, EventArgs.Empty);
        };
        handle.DragDelta += (_, e) =>
        {
            if (_index is null || TrackWidth <= 0)
            {
                return;
            }

            _dragX += e;
            var frame = _index.NearestBoundary(Math.Clamp((_dragX - TrackInset) / TrackWidth, 0, 1) * _index.Duration);
            FrameChanged?.Invoke(this, new(part, Math.Clamp(frame, handle.MinimumFrame, handle.MaximumFrame)));
        };
        handle.DragCompleted += (_, _) => InteractionCompleted?.Invoke(this, EventArgs.Empty);
        handle.FrameRequested += (_, frame) =>
        {
            InteractionStarted?.Invoke(this, EventArgs.Empty);
            FrameChanged?.Invoke(this, new(part, frame));
            InteractionCompleted?.Invoke(this, EventArgs.Empty);
        };
    }

    private void Track_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_index is null || TrackWidth <= 0)
        {
            return;
        }

        var x = e.GetCurrentPoint(Track).Position.X;
        var seconds = Math.Clamp((x - TrackInset) / TrackWidth, 0, 1) * _index.Duration;
        InteractionStarted?.Invoke(this, EventArgs.Empty);
        FrameChanged?.Invoke(this, new(TimelinePart.Playhead, _index.FrameAt(seconds)));
        InteractionCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private double TrackWidth => Math.Max(0, Root.ActualWidth - 2 * TrackInset);

    private double X(int frame) => TrackInset + (_index is null ? 0 : _index.TimeAt(frame) / _index.Duration * TrackWidth);

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => Render();

    private void Render()
    {
        if (_index is null)
        {
            return;
        }

        var start = X(_start);
        var end = X(_end);
        // Caps and hit areas sit outside the selected frames. Neither handle
        // overlaps the other, even when a one-frame selection is subpixel-wide.
        Canvas.SetLeft(StartHandle, start - TrimHitWidth);
        Canvas.SetLeft(EndHandle, end);
        RenderPlayhead();
        Canvas.SetLeft(Selection, start - TrimCapWidth);
        Selection.Width = Math.Max(0, end - start) + 2 * TrimCapWidth;
        Canvas.SetLeft(LeftShade, TrackInset);
        LeftShade.Width = Math.Max(0, start - TrackInset);
        Canvas.SetLeft(RightShade, end);
        RightShade.Width = Math.Max(0, X(_index.Count) - end);
    }
}
