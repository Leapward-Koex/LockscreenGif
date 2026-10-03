using System.Globalization;
using System.Text.RegularExpressions;

namespace LockscreenGif.Services;

public static partial class VideoFrameService
{
    public readonly record struct VideoMetadata(double Fps, uint Width, uint Height);

    [GeneratedRegex(
        @"^\[Parsed_showinfo_[0-9]+ @ [^\]]+\]\s+config in time_base:\s*[0-9]+/[0-9]+,\s+frame_rate:\s*([0-9]{1,10})/([0-9]{1,10})(?:\s|$)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
    )]
    private static partial Regex MetadataFrameRate();

    [GeneratedRegex(
        @"^\[Parsed_showinfo_[0-9]+ @ [^\]]+\]\s+n:\s*0\s+.*?\bduration_time:([-+0-9.eE]{1,32}|N/A)\s+.*?\bs:([0-9]{1,10})x([0-9]{1,10})(?:\s|$)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
    )]
    private static partial Regex MetadataFirstFrame();

    // Match the decoder and autorotation used by indexing, previews and exports. Only
    // request the first frame; obtaining metadata must not add a full indexing pass.
    public static Task<VideoMetadata> ReadMetadataAsync(string input, CancellationToken cancellationToken) =>
        Task.Run(() => ReadMetadataCoreAsync(input, cancellationToken), cancellationToken);

    private static async Task<VideoMetadata> ReadMetadataCoreAsync(string input, CancellationToken cancellationToken)
    {
        var fps = 0d;
        var firstDuration = 0d;
        uint width = 0;
        uint height = 0;
        await RunAsync(
                (string[])
                    [
                        "-i",
                        input,
                        "-map",
                        "0:v:0",
                        "-vf",
                        "showinfo=checksum=0",
                        "-frames:v",
                        "1",
                        "-an",
                        "-sn",
                        "-dn",
                        "-fps_mode",
                        "passthrough",
                        "-f",
                        "null",
                        "-",
                    ],
                line =>
                {
                    if (TryReadMetadataFrameRate(line, out var frameRate))
                    {
                        fps = frameRate;
                    }
                    if (width == 0 && TryReadMetadataFrame(line, out var frameWidth, out var frameHeight, out var duration))
                    {
                        width = frameWidth;
                        height = frameHeight;
                        firstDuration = duration;
                    }
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // Some variable-rate streams do not declare a nominal rate. The first decoded
        // duration is sufficient for the indexer's last-frame fallback; real timestamps
        // still determine every other frame boundary.
        if (fps == 0 && firstDuration > 0)
        {
            fps = 1 / firstDuration;
        }
        if (width == 0 || height == 0 || !double.IsFinite(fps) || fps <= 0)
        {
            throw new InvalidDataException("The video has no usable decoded dimensions or frame timing.");
        }
        return new VideoMetadata(fps, width, height);
    }

    internal static bool TryReadMetadataFrameRate(string line, out double fps)
    {
        fps = 0;
        if (line.Length > 4096)
        {
            return false;
        }
        var match = MetadataFrameRate().Match(line);
        if (
            !match.Success
            || !uint.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var numerator)
            || !uint.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var denominator)
            || numerator == 0
            || denominator == 0
        )
        {
            return false;
        }
        fps = (double)numerator / denominator;
        return true;
    }

    internal static bool TryReadMetadataFrame(string line, out uint width, out uint height, out double duration)
    {
        width = height = 0;
        duration = 0;
        if (line.Length > 4096)
        {
            return false;
        }
        var match = MetadataFirstFrame().Match(line);
        if (
            !match.Success
            || !uint.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var frameWidth)
            || !uint.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var frameHeight)
            || frameWidth is 0 or > int.MaxValue
            || frameHeight is 0 or > int.MaxValue
        )
        {
            return false;
        }
        width = frameWidth;
        height = frameHeight;
        if (
            double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var frameDuration)
            && double.IsFinite(frameDuration)
            && frameDuration > 0
        )
        {
            duration = frameDuration;
        }
        return true;
    }
}
