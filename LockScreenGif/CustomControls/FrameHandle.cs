using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace LockscreenGif.CustomControls;

/// <summary>A timeline handle with keyboard and UI Automation range support.</summary>
public sealed class FrameHandle : Control
{
    private uint? _pointer;
    private double _lastX;
    public event EventHandler? DragStarted;
    public event EventHandler<double>? DragDelta;
    public event EventHandler? DragCompleted;

    public FrameHandle() => ManipulationMode = ManipulationModes.None;

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _pointer is not null)
        {
            return;
        }

        if (!CapturePointer(e.Pointer))
        {
            return;
        }

        _pointer = e.Pointer.PointerId;
        _lastX = e.GetCurrentPoint(null).Position.X;
        Focus(FocusState.Pointer);
        DragStarted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId)
        {
            return;
        }

        var x = e.GetCurrentPoint(null).Position.X;
        var delta = x - _lastX;
        _lastX = x;
        DragDelta?.Invoke(this, delta);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId)
        {
            return;
        }

        CompleteDrag();
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e) => CompleteDrag();

    protected override void OnPointerCanceled(PointerRoutedEventArgs e) => CompleteDrag();

    private void CompleteDrag()
    {
        if (_pointer is null)
        {
            return;
        }

        _pointer = null;
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    public int FrameNumberOffset { get; set; } = 1;
    public int MinimumFrame { get; set; }
    public int MaximumFrame { get; set; }
    public int Frame { get; private set; }
    public event EventHandler<int>? FrameRequested;

    public void UpdateFrame(int frame)
    {
        var old = Frame;
        Frame = frame;
        if (old != frame && FrameworkElementAutomationPeer.FromElement(this) is { } peer)
        {
            peer.RaisePropertyChangedEvent(
                RangeValuePatternIdentifiers.ValueProperty,
                (double)old + FrameNumberOffset,
                (double)frame + FrameNumberOffset
            );
        }
    }

    internal void Request(int frame) => FrameRequested?.Invoke(this, Math.Clamp(frame, MinimumFrame, MaximumFrame));

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        var target = e.Key switch
        {
            VirtualKey.Left or VirtualKey.Down => Frame - 1,
            VirtualKey.Right or VirtualKey.Up => Frame + 1,
            VirtualKey.Home => MinimumFrame,
            VirtualKey.End => MaximumFrame,
            _ => (int?)null,
        };
        if (target is { } value)
        {
            Request(value);
            e.Handled = true;
        }
        else
        {
            base.OnKeyDown(e);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameHandlePeer(this);

    private sealed class FrameHandlePeer(FrameHandle owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(FrameHandle);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

        protected override object GetPatternCore(PatternInterface pattern) =>
            pattern == PatternInterface.RangeValue ? this : base.GetPatternCore(pattern);

        public bool IsReadOnly => !owner.IsEnabled;
        public double LargeChange => 10;
        public double SmallChange => 1;
        public double Minimum => owner.MinimumFrame + owner.FrameNumberOffset;
        public double Maximum => owner.MaximumFrame + owner.FrameNumberOffset;
        public double Value => owner.Frame + owner.FrameNumberOffset;

        public void SetValue(double value)
        {
            if (IsReadOnly || !double.IsFinite(value) || value < Minimum || value > Maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            owner.DispatcherQueue.TryEnqueue(() => owner.Request((int)Math.Round(value) - owner.FrameNumberOffset));
        }
    }
}
