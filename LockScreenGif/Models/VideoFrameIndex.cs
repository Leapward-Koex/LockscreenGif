using System.Globalization;

namespace LockscreenGif.Models;

/// <summary>Decoded presentation order. Trim intervals include Start and exclude End.</summary>
public sealed class VideoFrameIndex
{
    private readonly double[] _boundaries;
    public int Count => _boundaries.Length - 1;
    public double Duration => _boundaries[^1];

    // FFmpeg's decoded timeline can start after another stream in the input.
    // Keep this origin for input seeking while editing uses zero-based times.
    public double SourceStartTime { get; }

    public VideoFrameIndex(IEnumerable<double> timestamps, double lastFrameDuration)
    {
        var times = timestamps.ToArray();
        if (times.Length == 0 || !double.IsFinite(lastFrameDuration) || lastFrameDuration <= 0)
        {
            throw new ArgumentException("The video has no usable frame timing.");
        }

        _boundaries = new double[times.Length + 1];
        for (var i = 0; i < times.Length; i++)
        {
            if (!double.IsFinite(times[i]) || (i > 0 && times[i] <= times[i - 1]))
            {
                throw new ArgumentException("The video's frame timestamps are not strictly increasing.");
            }

            _boundaries[i] = times[i] - times[0];
        }
        SourceStartTime = times[0];
        _boundaries[^1] = _boundaries[^2] + lastFrameDuration;
    }

    public double TimeAt(int boundary) => _boundaries[Math.Clamp(boundary, 0, Count)];

    public int NearestBoundary(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        var found = Array.BinarySearch(_boundaries, seconds);
        if (found >= 0)
        {
            return found;
        }

        var next = ~found;
        if (next == 0)
        {
            return 0;
        }

        if (next >= _boundaries.Length)
        {
            return Count;
        }

        return seconds - _boundaries[next - 1] <= _boundaries[next] - seconds ? next - 1 : next;
    }

    public int FrameAt(double seconds)
    {
        var found = Array.BinarySearch(_boundaries, seconds);
        return Math.Clamp(found >= 0 ? found : ~found - 1, 0, Count - 1);
    }

    public void ValidateRange(int start, int end)
    {
        if (start < 0 || end > Count || start >= end)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "Select at least one video frame.");
        }
    }

    public double[] PresentationTimes(int start, int end)
    {
        ValidateRange(start, end);
        return Enumerable.Range(start, end - start).Select(i => TimeAt(i) - TimeAt(start)).ToArray();
    }

    public static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        var suffix = time.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
        return seconds >= 3600 ? ((long)time.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + suffix : suffix;
    }

    public static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            var style = i == parts.Length - 1 ? NumberStyles.AllowDecimalPoint : NumberStyles.None;
            if (
                !double.TryParse(parts[i], style, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value)
                || value < 0
                || (i > 0 && value >= 60)
            )
            {
                return false;
            }

            seconds = seconds * 60 + value;
        }
        return double.IsFinite(seconds) && seconds <= TimeSpan.MaxValue.TotalSeconds;
    }
}
