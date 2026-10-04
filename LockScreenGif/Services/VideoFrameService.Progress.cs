using System.Globalization;
using System.Text.RegularExpressions;
using LockscreenGif.Models;

namespace LockscreenGif.Services;

public static partial class VideoFrameService
{
    [GeneratedRegex(
        @"^\s*Duration:\s*(\d{1,10}):([0-5]\d):([0-5]\d(?:\.\d{1,10})?),\s*start:\s*([-+]?\d{1,15}(?:\.\d{1,15})?)(?:,|\s|$)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
    )]
    private static partial Regex InputDuration();

    // Consume the header already emitted by this indexing process. A second
    // metadata pass would add startup work just to display a progress estimate.
    internal sealed class IndexProgressTracker
    {
        private double? _duration;
        private double? _firstTimestamp;
        private bool _headerRead;

        public void ReadHeader(string line)
        {
            if (
                _headerRead
                || _firstTimestamp is not null
                || line.Length > 4096
                || !line.TrimStart().StartsWith("Duration:", StringComparison.Ordinal)
            )
            {
                return;
            }

            _headerRead = true;
            var match = InputDuration().Match(line);
            if (!match.Success)
            {
                return;
            }
            var start = double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
            // For nonzero container starts, Duration can mean either an absolute
            // end time or an elapsed duration depending on the demuxer. Do not
            // turn that ambiguity into a misleading percentage.
            if (start != 0)
            {
                return;
            }
            var duration =
                double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                + double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            if (double.IsFinite(duration) && duration > 0)
            {
                _duration = duration;
            }
        }

        public VideoIndexProgress Frame(int count, double timestamp, double frameDuration)
        {
            _firstTimestamp ??= timestamp;
            double? fraction = null;
            if (
                _duration is { } duration
                && _firstTimestamp is >= 0
                && duration > _firstTimestamp.Value
                && double.IsFinite(timestamp)
                && double.IsFinite(frameDuration)
                && frameDuration > 0
            )
            {
                // The first video frame may follow an earlier audio stream. Both
                // PTS and a zero-start container duration share the input origin.
                var videoDuration = duration - _firstTimestamp.Value;
                fraction = Math.Clamp((timestamp - _firstTimestamp.Value + frameDuration) / videoDuration, 0, 0.99);
            }
            return new(count, fraction);
        }
    }
}
