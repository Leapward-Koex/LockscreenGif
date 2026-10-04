using System.Globalization;
using System.Text.RegularExpressions;
using LockscreenGif.Models;
using LockscreenGif.Services;

internal static class IndexingTests
{
    private const double FractionalFps = 30000d / 1001;

    public static async Task RunAsync(string fractional, string variable)
    {
        await CheckIndexAsync(fractional, FractionalFps, 12, "FFV1 fractional rate");
        await CheckIndexAsync(variable, 25, 8, "FFV1 variable rate");

        var root = TempDirectoryService.GetAppTempRoot();
        var h264 = Path.Combine(root, "index-h264.mp4");
        await CreateVideoAsync(
            h264,
            "30000/1001",
            ["-c:v", "libx264", "-preset", "fast", "-bf", "3", "-g", "60", "-x264-params", "b-adapt=0:scenecut=0"]
        );
        await CheckIndexAsync(h264, FractionalFps, 18, "H.264 fractional rate with B frames", requireBFrames: true);
        await CheckPreviewAsync(h264, "H.264");

        // Matroska retains the trailing reordered frame without MP4 edit-list clipping.
        var h264Variable = Path.Combine(root, "index-h264-variable.mkv");
        await CreateVideoAsync(
            h264Variable,
            "25",
            [
                "-vf",
                "setpts=if(lt(N\\,6)\\,N\\,6+(N-6)*3)",
                "-fps_mode",
                "passthrough",
                "-c:v",
                "libx264",
                "-preset",
                "fast",
                "-bf",
                "3",
                "-g",
                "60",
                "-x264-params",
                "b-adapt=0:scenecut=0",
            ]
        );
        var variableIndex = await CheckIndexAsync(h264Variable, 25, 18, "H.264 variable rate with B frames", requireBFrames: true);
        Check(
            Math.Abs(variableIndex.TimeAt(2) - variableIndex.TimeAt(1) - 0.04) < 0.000001
                && Math.Abs(variableIndex.TimeAt(8) - variableIndex.TimeAt(7) - 0.12) < 0.000001,
            "H.264 variable-rate fixture retains both frame intervals"
        );

        var delayed = Path.Combine(root, "index-h264-delayed.mp4");
        await VideoFrameService.RunAsync(["-i", h264, "-c", "copy", "-output_ts_offset", "3600.1", delayed], null, CancellationToken.None);
        var delayedIndex = await CheckIndexAsync(delayed, FractionalFps, 18, "H.264 nonzero source start");
        Check(delayedIndex.TimeAt(0) == 0, "Nonzero source starts normalize to the first decoded frame");

        var rotated = Path.Combine(root, "index-h264-rotated.mp4");
        await VideoFrameService.RunAsync(["-display_rotation:v:0", "90", "-i", h264, "-c", "copy", rotated], null, CancellationToken.None);
        await CheckIndexAsync(rotated, FractionalFps, 18, "Rotated H.264");
        await CheckPreviewAsync(rotated, "Rotated H.264");
        var rotatedMetadata = await VideoFrameService.ReadMetadataAsync(rotated, CancellationToken.None);
        Check(rotatedMetadata.Width == 48 && rotatedMetadata.Height == 64, "Timing-only indexing keeps full-quality metadata autorotation");

        var hevc = Path.Combine(root, "index-hevc.mp4");
        await CreateVideoAsync(
            hevc,
            "30000/1001",
            [
                "-c:v",
                "libx265",
                "-preset",
                "ultrafast",
                "-x265-params",
                "pools=none:frame-threads=1:bframes=3:b-adapt=0:scenecut=0:log-level=error",
            ]
        );
        await CheckIndexAsync(hevc, FractionalFps, 18, "HEVC fractional rate with B frames", requireBFrames: true);
        await CheckPreviewAsync(hevc, "HEVC");

        var mpeg4 = Path.Combine(root, "index-mpeg4.mp4");
        await CreateVideoAsync(mpeg4, "25", ["-c:v", "mpeg4", "-bf", "2"]);
        await CheckIndexAsync(mpeg4, 25, 18, "MPEG4 with B frames", requireBFrames: true);

        var vp9 = Path.Combine(root, "index-vp9.webm");
        await CreateVideoAsync(vp9, "30000/1001", ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8"]);
        await CheckIndexAsync(vp9, FractionalFps, 18, "VP9 fractional rate");
    }

    private static Task CreateVideoAsync(string output, string rate, string[] encodingArguments) =>
        VideoFrameService.RunAsync(
            new[] { "-f", "lavfi", "-i", "testsrc2=size=64x48:rate=" + rate, "-frames:v", "18" }.Concat(encodingArguments).Append(output),
            null,
            CancellationToken.None
        );

    private static async Task<VideoFrameIndex> CheckIndexAsync(
        string video,
        double nominalFps,
        int expectedCount,
        string description,
        bool requireBFrames = false
    )
    {
        var timestamps = new List<double>();
        var timeBase = 0d;
        var lastDuration = 1 / nominalFps;
        var hasBFrames = false;
        await VideoFrameService.RunAsync(
            [
                "-skip_loop_filter",
                "default",
                "-autorotate",
                "-i",
                video,
                "-map",
                "0:v:0",
                "-vf",
                "showinfo=checksum=0",
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
                var configuration = Regex.Match(line, @"config in time_base:\s*(\d+)/(\d+)", RegexOptions.CultureInvariant);
                if (configuration.Success)
                {
                    timeBase =
                        double.Parse(configuration.Groups[1].Value, CultureInfo.InvariantCulture)
                        / double.Parse(configuration.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                if (!VideoFrameService.TryReadFrame(line, out var number, out var time, out var duration, timeBase))
                {
                    return;
                }
                if (number != timestamps.Count || timeBase <= 0)
                {
                    throw new InvalidDataException("Full-quality reference did not provide ordered frames with an integer time base.");
                }

                timestamps.Add(time);
                lastDuration = duration > 0 ? duration : 1 / nominalFps;
                hasBFrames |= line.Contains("type:B", StringComparison.Ordinal);
            },
            CancellationToken.None
        );
        var reference = new VideoFrameIndex(timestamps, lastDuration);
        var actual = await VideoFrameService.IndexAsync(video, nominalFps, null, CancellationToken.None);
        if (actual.Count != expectedCount || actual.Count != reference.Count)
        {
            throw new InvalidOperationException(
                $"FAILED: {description} decoded {actual.Count} indexed and {reference.Count} reference frames; expected {expectedCount}."
            );
        }
        if (requireBFrames && !hasBFrames)
        {
            throw new InvalidOperationException($"FAILED: {description} fixture contains no B frames.");
        }
        Check(
            Enumerable.Range(0, actual.Count + 1).All(i => Math.Abs(actual.TimeAt(i) - reference.TimeAt(i)) < 0.000000001),
            description + " indexing matches every full-quality presentation timestamp and final duration"
        );
        return actual;
    }

    private static async Task CheckPreviewAsync(string video, string description)
    {
        using var reference = new MemoryStream();
        await VideoFrameService.RunAsync(
            [
                "-skip_loop_filter",
                "default",
                "-autorotate",
                "-i",
                video,
                "-map",
                "0:v:0",
                "-vf",
                "trim=start_frame=3:end_frame=6,scale=560:-2",
                "-frames:v",
                "3",
                "-an",
                "-sn",
                "-dn",
                "-fps_mode",
                "passthrough",
                "-c:v",
                "png",
                "-f",
                "image2pipe",
                "pipe:1",
            ],
            null,
            CancellationToken.None,
            reference
        );
        var images = await VideoFrameService.PreviewWindowAsync(video, 3, 6, CancellationToken.None);
        Check(
            images.Count == 3 && reference.ToArray().SequenceEqual(images.SelectMany(image => image)),
            description + " preview retains full-quality decoded images and orientation"
        );
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("FAILED: " + message);
        }
        Console.WriteLine("PASS " + message);
    }
}
