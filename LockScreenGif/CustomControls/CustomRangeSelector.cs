using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;


using System;
using System.Globalization;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace LockscreenGif.CustomControls;

/// <summary>
/// A RangeSelector that raises a continuous <see cref="RangeDragging"/> event
/// while either thumb is moving and lets you format / template the tooltip
/// content.
/// </summary>
public sealed class CustomRangeSelector : RangeSelector
{
    #region public API -------------------------------------------------------------------------

    /// <summary>
    /// Raised every time either thumb moves (≈ DragDelta).
    /// </summary>
    public event EventHandler<RangeDraggingEventArgs>? RangeDragging;

    #endregion ----------------------------------------------------------------------------------

    private Thumb? _minThumb;
    private Thumb? _maxThumb;
    private TextBlock? _toolTipText;


    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // Get template parts
        _minThumb = GetTemplateChild("MinThumb") as Thumb;
        _maxThumb = GetTemplateChild("MaxThumb") as Thumb;
        _toolTipText = GetTemplateChild("ToolTipText") as TextBlock;

        if (_minThumb is not null)
        {
            _minThumb.DragDelta += MinThumb_DragDelta;
            _minThumb.DragStarted += Thumb_DragStarted;
        }

        if (_maxThumb is not null)
        {
            _maxThumb.DragDelta += MaxThumb_DragDelta;
            _maxThumb.DragStarted += Thumb_DragStarted;
        }

        UpdateToolTip(RangeStart, RangeEnd);
    }

    private void MinThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        // Fire custom event so external code gets continuous updates
        RangeDragging?.Invoke(
            this,
            new RangeDraggingEventArgs(RangeStart, RangeSelectorProperty.MinimumValue));

        // Update the tooltip immediately for smoother visual feedback
        UpdateToolTip(RangeStart, RangeEnd);
    }

    private void MaxThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        // Fire custom event so external code gets continuous updates
        RangeDragging?.Invoke(
            this,
            new RangeDraggingEventArgs(RangeEnd, RangeSelectorProperty.MaximumValue));

        // Update the tooltip immediately for smoother visual feedback
        UpdateToolTip(RangeStart, RangeEnd);
    }

    private void Thumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        UpdateToolTip(RangeStart, RangeEnd);
    }

    #region tooltip helpers --------------------------------------------------------------------

    private static string ToMinSec(double seconds)
    {
        //  m = minutes (no leading 0 beyond the first digit)
        // ss = seconds 00-59 with leading zero
        // .f = tenths
        return TimeSpan.FromSeconds(seconds).ToString(@"m\:ss\.f");
    }

    private void UpdateToolTip(double start, double end)
    {
        if (_toolTipText is not null)
        {
            _toolTipText.Text = $"{ToMinSec(start)} – {ToMinSec(end)}";
        }
    }

    #endregion ---------------------------------------------------------------------------------
}

/// <summary>
/// Event args for <see cref="FineRangeSelector.RangeDragging"/>.
/// </summary>
public sealed class RangeDraggingEventArgs : EventArgs
{
    public double NewValue
    {
        get;
    }

    public RangeSelectorProperty ChangedRangeProperty
    {
        get;
    }

    internal RangeDraggingEventArgs(double newValue, RangeSelectorProperty changedProperty)
        => (NewValue, ChangedRangeProperty) = (newValue, changedProperty);
}
