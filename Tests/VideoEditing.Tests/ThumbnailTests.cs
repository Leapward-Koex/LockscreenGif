using System.IO.Pipes;
using LockscreenGif.Models;
using LockscreenGif.Services;

internal static class ThumbnailTests
{
    public static async Task RunAsync(string cfr, VideoFrameIndex cfrIndex, string vfr, VideoFrameIndex vfrIndex)
    {
        await CheckThumbnailsAsync(cfr, cfrIndex, "fractional FPS");
        await CheckThumbnailsAsync(vfr, vfrIndex, "variable frame durations");
        await CheckSingleFrameAsync();
        await CheckInterframeVideoAsync();
        await CheckDelayedVideoAsync();
        await CheckCancellationAsync(cfr, cfrIndex);
    }

    private static async Task CheckThumbnailsAsync(string input, VideoFrameIndex index, string description)
    {
        var frames = Enumerable.Range(0, 8).Select(i => index.FrameAt(index.Duration * i / 8)).Distinct().ToArray();
        var thumbnails = await VideoFrameService.ThumbnailsAsync(input, index, CancellationToken.None);
        Check(thumbnails.Count == frames.Length, description + " thumbnail count preserves deduplicated source frames");
        for (var i = 0; i < frames.Length; i++)
        {
            var expected = await ReadSourceFrameAsync(input, frames[i]);
            Check(thumbnails[i].SequenceEqual(expected), description + " thumbnail order and pixels match source frame " + frames[i]);
            var sought = await VideoFrameService.SeekThumbnailAsync(input, index, frames[i], CancellationToken.None);
            Check(sought.SequenceEqual(expected), description + " timestamp seek matches source frame " + frames[i]);
        }
    }

    private static async Task<byte[]> ReadSourceFrameAsync(string input, int frame)
    {
        // Decode from the beginning and select by presentation ordinal. This is
        // independent of timestamp seeking and supplies the exact source image.
        using var output = new MemoryStream();
        await VideoFrameService.RunAsync(
            [
                "-i",
                input,
                "-map",
                "0:v:0",
                "-vf",
                $"select=eq(n\\,{frame}),scale=160:-2",
                "-frames:v",
                "1",
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
            output
        );
        var image = output.ToArray();
        if (image.Length <= 8 || !image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            throw new InvalidDataException("The thumbnail baseline did not emit a PNG.");
        }
        return image;
    }

    private static async Task CheckSingleFrameAsync()
    {
        var input = Path.Combine(TempDirectoryService.GetAppTempRoot(), "thumbnail-single.mkv");
        await VideoFrameService.RunAsync(
            ["-f", "lavfi", "-i", "testsrc2=size=64x48:rate=25", "-frames:v", "1", "-c:v", "ffv1", input],
            null,
            CancellationToken.None
        );
        var index = await VideoFrameService.IndexAsync(input, 25, null, CancellationToken.None);
        Check(index.Count == 1, "single-frame thumbnail fixture contains one decoded frame");
        await CheckThumbnailsAsync(input, index, "single frame");
    }

    private static async Task CheckInterframeVideoAsync()
    {
        var input = Path.Combine(TempDirectoryService.GetAppTempRoot(), "thumbnail-long-gop.mp4");
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
        Check(index.Count == 640, "long GOP thumbnail fixture exercises extraction beyond 512 frames");
        await CheckThumbnailsAsync(input, index, "fractional FPS with B frames and long GOP");
        var last = await VideoFrameService.SeekThumbnailAsync(input, index, index.Count - 1, CancellationToken.None);
        var expectedLast = await ReadSourceFrameAsync(input, index.Count - 1);
        Check(last.SequenceEqual(expectedLast), "timestamp seek retains the final image in a B-frame video");
    }

    private static async Task CheckDelayedVideoAsync()
    {
        var input = Path.Combine(TempDirectoryService.GetAppTempRoot(), "thumbnail-delayed-video.mkv");
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
        Check(
            index.SourceStartTime >= 0.99 && index.SourceStartTime <= 1.01 && index.TimeAt(0) == 0,
            "video source offset survives normalized editor timing"
        );
        await CheckThumbnailsAsync(input, index, "video delayed after audio");
    }

    private static async Task CheckCancellationAsync(string input, VideoFrameIndex index)
    {
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        await ExpectCancellationAsync(
            VideoFrameService.ThumbnailsAsync(input, index, preCancelled.Token),
            "pre-cancelled thumbnail strip produces no results"
        );
        await ExpectCancellationAsync(
            VideoFrameService.SeekThumbnailAsync(input, index, 1, preCancelled.Token),
            "pre-cancelled thumbnail seek produces no result"
        );

        // A pipe with no supplied header proves cancellation stops an active
        // seeking decoder while its input read is blocked.
        var pipeName = "LockscreenGif-thumbnail-test-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reading = VideoFrameService.SeekThumbnailAsync(@"\\.\pipe\" + pipeName, new VideoFrameIndex([0], 0.04), 0, cancellation.Token);
        try
        {
            await pipe.WaitForConnectionAsync(cancellation.Token);
            cancellation.Cancel();
            await ExpectCancellationAsync(
                reading.WaitAsync(TimeSpan.FromSeconds(5)),
                "active thumbnail seek cancellation waits for the blocked decoder to exit"
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
            throw new InvalidOperationException("FAILED: Thumbnail extraction ignored cancellation.");
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
