using System.Buffers.Binary;
using System.IO.Pipes;
using LockscreenGif.Models;
using LockscreenGif.Services;

internal static class PreviewWindowTests
{
    public static async Task RunAsync(string cfr, VideoFrameIndex cfrIndex, string vfr, VideoFrameIndex vfrIndex)
    {
        await CheckWindowsAsync(cfr, cfrIndex, "fractional FPS");
        await CheckWindowsAsync(vfr, vfrIndex, "variable frame durations");
        await CheckInterframeVideoAsync();
        await CheckDelayedVideoAsync();
        await CheckRotatedVideoAsync();
        await CheckInvalidRangesAsync(cfr, cfrIndex);
        await CheckCancellationAsync(cfr, cfrIndex);
    }

    private static async Task CheckWindowsAsync(string input, VideoFrameIndex index, string description)
    {
        var middle = index.Count / 2;
        foreach (
            var (start, end) in new[]
            {
                (0, Math.Min(4, index.Count)),
                (middle, Math.Min(middle + 4, index.Count)),
                (Math.Max(0, index.Count - 4), index.Count),
                (index.Count - 1, index.Count),
            }.Distinct()
        )
        {
            // The ordinal implementation decodes from the beginning. Keep it as
            // an independent baseline for the indexed seek and every nearby frame.
            var expected = await VideoFrameService.PreviewWindowAsync(input, start, end, CancellationToken.None);
            var actual = await VideoFrameService.PreviewWindowAsync(input, index, start, end, CancellationToken.None);
            CheckFrames(actual, expected, $"{description} preview [{start}, {end})");
        }
    }

    private static void CheckFrames(IReadOnlyList<byte[]> actual, IReadOnlyList<byte[]> expected, string description)
    {
        Check(actual.Count == expected.Count, description + " returns exactly the selected frames");
        for (var i = 0; i < actual.Count; i++)
        {
            Check(
                BinaryPrimitives.ReadInt32BigEndian(actual[i].AsSpan(16, 4)) == 560 && actual[i].SequenceEqual(expected[i]),
                description + " frame " + i + " matches the full-resolution ordinal preview"
            );
        }
    }

    private static async Task CheckInterframeVideoAsync()
    {
        var input = Path.Combine(TempDirectoryService.GetAppTempRoot(), "preview-long-gop.mp4");
        await VideoFrameService.RunAsync(
            [
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=64x48:rate=30000/1001",
                "-frames:v",
                "640",
                "-c:v",
                "libx264",
                "-preset",
                "ultrafast",
                "-g",
                "250",
                "-keyint_min",
                "250",
                "-sc_threshold",
                "0",
                "-bf",
                "3",
                input,
            ],
            null,
            CancellationToken.None
        );
        var index = await VideoFrameService.IndexAsync(input, 30000d / 1001, null, CancellationToken.None);
        Check(index.Count == 640, "long GOP preview fixture contains 640 frames with B-frame reordering");
        await CheckWindowsAsync(input, index, "fractional FPS with B frames and long GOP");

        // These windows straddle keyframes and include a late range beyond 512.
        foreach (var (start, end) in new[] { (248, 253), (498, 503), (520, 525) })
        {
            var expected = await VideoFrameService.PreviewWindowAsync(input, start, end, CancellationToken.None);
            var actual = await VideoFrameService.PreviewWindowAsync(input, index, start, end, CancellationToken.None);
            CheckFrames(actual, expected, $"long GOP preview [{start}, {end})");
        }

        // A valid normalized index with its source origin past EOF forces the
        // seek decoder to return no images. The ordinal fallback must still work.
        var shifted = new VideoFrameIndex(
            Enumerable.Range(0, index.Count).Select(frame => index.SourceStartTime + index.Duration + 10 + index.TimeAt(frame)),
            index.TimeAt(index.Count) - index.TimeAt(index.Count - 1)
        );
        var fallbackExpected = await VideoFrameService.PreviewWindowAsync(input, 520, 525, CancellationToken.None);
        var fallbackActual = await VideoFrameService.PreviewWindowAsync(input, shifted, 520, 525, CancellationToken.None);
        CheckFrames(fallbackActual, fallbackExpected, "preview falls back after a seek beyond the source duration");
    }

    private static async Task CheckDelayedVideoAsync()
    {
        var input = Path.Combine(TempDirectoryService.GetAppTempRoot(), "preview-delayed-video.mkv");
        await VideoFrameService.RunAsync(
            [
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=64x48:rate=25:duration=2",
                "-f",
                "lavfi",
                "-i",
                "sine=frequency=1000:duration=3",
                "-map",
                "0:v:0",
                "-map",
                "1:a:0",
                "-vf",
                "setpts=PTS+1/TB",
                "-fps_mode",
                "passthrough",
                "-c:v",
                "ffv1",
                "-c:a",
                "pcm_s16le",
                input,
            ],
            null,
            CancellationToken.None
        );
        var index = await VideoFrameService.IndexAsync(input, 25, null, CancellationToken.None);
        Check(index.SourceStartTime >= 0.99 && index.SourceStartTime <= 1.01, "preview fixture retains delayed video source origin");
        await CheckWindowsAsync(input, index, "video delayed after audio");
    }

    private static async Task CheckRotatedVideoAsync()
    {
        var root = TempDirectoryService.GetAppTempRoot();
        var original = Path.Combine(root, "preview-unrotated.mp4");
        var rotated = Path.Combine(root, "preview-rotated.mp4");
        await VideoFrameService.RunAsync(
            ["-f", "lavfi", "-i", "testsrc2=size=64x48:rate=25", "-frames:v", "12", "-c:v", "mpeg4", original],
            null,
            CancellationToken.None
        );
        await VideoFrameService.RunAsync(
            ["-display_rotation:v:0", "90", "-i", original, "-c", "copy", rotated],
            null,
            CancellationToken.None
        );
        var index = await VideoFrameService.IndexAsync(rotated, 25, null, CancellationToken.None);
        await CheckWindowsAsync(rotated, index, "rotated video");
        var preview = await VideoFrameService.PreviewWindowAsync(rotated, index, 3, 4, CancellationToken.None);
        Check(
            BinaryPrimitives.ReadInt32BigEndian(preview[0].AsSpan(20, 4)) > 560,
            "indexed preview preserves the decoder's automatic portrait rotation"
        );
    }

    private static async Task CheckInvalidRangesAsync(string input, VideoFrameIndex index)
    {
        foreach (var (start, end) in new[] { (-1, 1), (0, 0), (3, 2), (0, index.Count + 1), (index.Count, index.Count) })
        {
            try
            {
                await VideoFrameService.PreviewWindowAsync(input, index, start, end, CancellationToken.None);
                throw new InvalidOperationException("FAILED: Indexed preview accepted an invalid frame interval.");
            }
            catch (ArgumentOutOfRangeException)
            {
                Check(true, $"indexed preview rejects invalid range [{start}, {end})");
            }
        }
    }

    private static async Task CheckCancellationAsync(string input, VideoFrameIndex index)
    {
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        await ExpectCancellationAsync(
            VideoFrameService.PreviewWindowAsync(input, index, 1, 4, preCancelled.Token),
            "pre-cancelled indexed preview produces no frames"
        );

        // An input pipe without a video header blocks a real decoder read. Wait
        // for its connection before cancelling so this covers active processing.
        var pipeName = "LockscreenGif-preview-test-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reading = VideoFrameService.PreviewWindowAsync(@"\\.\pipe\" + pipeName, index, 1, 4, cancellation.Token);
        try
        {
            await pipe.WaitForConnectionAsync(cancellation.Token);
            cancellation.Cancel();
            await ExpectCancellationAsync(
                reading.WaitAsync(TimeSpan.FromSeconds(5)),
                "active indexed preview cancellation waits for the blocked decoder to exit"
            );
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await reading.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task ExpectCancellationAsync(Task operation, string message)
    {
        try
        {
            await operation;
            throw new InvalidOperationException("FAILED: Indexed preview ignored cancellation.");
        }
        catch (OperationCanceledException)
        {
            Check(true, message);
        }
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
