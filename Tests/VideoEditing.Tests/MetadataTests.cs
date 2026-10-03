using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using LockscreenGif.Services;

internal static class MetadataTests
{
    public static async Task RunAsync(string cfr, string vfr)
    {
        CheckParsers();
        var constant = await VideoFrameService.ReadMetadataAsync(cfr, CancellationToken.None);
        Check(constant.Width == 64 && constant.Height == 48, "FFmpeg metadata reads decoded dimensions");
        Check(Math.Abs(constant.Fps - 30000d / 1001) < 0.000001, "FFmpeg metadata retains fractional frame rates");
        var variable = await VideoFrameService.ReadMetadataAsync(vfr, CancellationToken.None);
        Check(variable.Width == 64 && variable.Height == 48 && variable.Fps > 0, "VFR metadata provides usable nominal timing");
        var index = await VideoFrameService.IndexAsync(vfr, variable.Fps, null, CancellationToken.None);
        Check(Math.Abs(index.TimeAt(5) - index.TimeAt(4) - 0.12) < 0.000001, "Metadata fallback preserves VFR frame indexing");

        await CheckRotationAsync();
        await CheckInvalidInputAsync();
        await CheckCancellationAsync(cfr);
        await CheckUiContextAsync(cfr);
    }

    private static void CheckParsers()
    {
        const string prefix = "[Parsed_showinfo_0 @ 0000000000000001] ";
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check(
                VideoFrameService.TryReadMetadataFrameRate(prefix + "config in time_base: 1/30000, frame_rate: 30000/1001", out var fps)
                    && Math.Abs(fps - 30000d / 1001) < 0.000001,
                "Metadata rational parsing is culture independent"
            );
            foreach (var rational in new[] { "0/0", "1/0", "4294967296/1", "1/4294967296", "1/10000000000", "NaN/1", "-30/1" })
            {
                Check(
                    !VideoFrameService.TryReadMetadataFrameRate(prefix + "config in time_base: 1/1000, frame_rate: " + rational, out _),
                    "Metadata rejects invalid or overflowing FPS " + rational
                );
            }
            Check(
                !VideoFrameService.TryReadMetadataFrameRate(prefix + "config out time_base: 1/1000, frame_rate: 1/1", out _),
                "Output filter configuration cannot replace input frame rate"
            );
            Check(
                !VideoFrameService.TryReadMetadataFrameRate("filename config in time_base: 1/1000, frame_rate: 30/1", out _),
                "Unrelated stderr cannot provide frame metadata"
            );
            const string frame = "n: 0 pts: 0 pts_time:0 duration: 40 duration_time:0.04 fmt:yuv420p sar:1/1 s:";
            Check(
                VideoFrameService.TryReadMetadataFrame(prefix + frame + "64x48 i:P", out var width, out var height, out var duration)
                    && width == 64
                    && height == 48
                    && duration == 0.04,
                "Metadata first-frame duration parsing is culture independent"
            );
            foreach (var dimensions in new[] { "0x48", "64x0", "4294967296x48", "64x2147483648", "64x10000000000" })
            {
                Check(
                    !VideoFrameService.TryReadMetadataFrame(prefix + frame + dimensions + " i:P", out _, out _, out _),
                    "Metadata rejects invalid or overflowing dimensions " + dimensions
                );
            }
            Check(
                !VideoFrameService.TryReadMetadataFrame(prefix + frame.Replace("n: 0", "n: 1") + "64x48 i:P", out _, out _, out _),
                "Metadata dimensions are taken only from the first frame"
            );
            Check(
                !VideoFrameService.TryReadMetadataFrame(prefix + frame + "64x48 " + new string('x', 4096), out _, out _, out _),
                "Metadata parsing rejects oversized diagnostic lines"
            );
            Check(
                VideoFrameService.TryReadMetadataFrame(
                    prefix + frame.Replace("duration_time:0.04", "duration_time:1e999") + "64x48 i:P",
                    out _,
                    out _,
                    out duration
                )
                    && duration == 0,
                "Non-finite frame duration cannot become fallback FPS"
            );
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    private static async Task CheckRotationAsync()
    {
        var root = TempDirectoryService.GetAppTempRoot();
        var original = Path.Combine(root, "metadata-unrotated.mp4");
        var rotated = Path.Combine(root, "metadata-rotated.mp4");
        await VideoFrameService.RunAsync(
            ["-f", "lavfi", "-i", "testsrc2=size=64x48:rate=25", "-frames:v", "2", "-c:v", "mpeg4", original],
            null,
            CancellationToken.None
        );
        await VideoFrameService.RunAsync(
            ["-display_rotation:v:0", "90", "-i", original, "-c", "copy", rotated],
            null,
            CancellationToken.None
        );
        var metadata = await VideoFrameService.ReadMetadataAsync(rotated, CancellationToken.None);
        Check(metadata.Width == 48 && metadata.Height == 64, "Metadata accounts for the decoder's automatic rotation");
        var index = await VideoFrameService.IndexAsync(rotated, metadata.Fps, null, CancellationToken.None);
        var exported = await VideoFrameService.ExportAsync(rotated, index, 0, 1, (int)metadata.Width, 0, _ => { });
        var png = await File.ReadAllBytesAsync(Directory.GetFiles(exported.Directory, "*.png").Single());
        Check(
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) == metadata.Width
                && BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) == metadata.Height,
            "Metadata and exported frames use the same orientation"
        );
    }

    private static async Task CheckInvalidInputAsync()
    {
        var root = TempDirectoryService.GetAppTempRoot();
        var corrupt = Path.Combine(root, "synthetic-private-metadata-invalid.mkv");
        await File.WriteAllTextAsync(corrupt, "This is deliberately not a video.");
        await ExpectNativeFailureAsync(corrupt, "Corrupt metadata input retains the FFmpeg native failure");
        var audio = Path.Combine(root, "metadata-audio-only.wav");
        await VideoFrameService.RunAsync(["-f", "lavfi", "-i", "sine=frequency=1000:duration=0.1", audio], null, CancellationToken.None);
        await ExpectNativeFailureAsync(audio, "Metadata input without a video stream retains the FFmpeg native failure");
    }

    private static async Task ExpectNativeFailureAsync(string path, string message)
    {
        try
        {
            await VideoFrameService.ReadMetadataAsync(path, CancellationToken.None);
            throw new InvalidOperationException("FAILED: Metadata accepted invalid video input.");
        }
        catch (MediaProcessingException ex)
        {
            Check(ex.Component == MediaProcessingComponent.Ffmpeg && ex.ErrorCode != 0, message);
        }
    }

    private static async Task CheckCancellationAsync(string validVideo)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await VideoFrameService.ReadMetadataAsync(validVideo, cancelled.Token);
            throw new InvalidOperationException("FAILED: Metadata ignored pre-cancellation.");
        }
        catch (OperationCanceledException)
        {
            Check(true, "Pre-cancelled metadata requests produce no result");
        }

        // A pipe that supplies no header keeps FFmpeg in an actual blocking read,
        // proving cancellation stops the process, not merely a queued Task.Run.
        var pipeName = "LockscreenGif-metadata-test-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reading = VideoFrameService.ReadMetadataAsync(@"\\.\pipe\" + pipeName, cancellation.Token);
        try
        {
            await pipe.WaitForConnectionAsync(cancellation.Token);
            cancellation.Cancel();
            try
            {
                await reading.WaitAsync(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("FAILED: Metadata ignored active cancellation.");
            }
            catch (OperationCanceledException)
            {
                Check(true, "Active metadata cancellation stops and waits for the blocked FFmpeg process");
            }
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

    private static async Task CheckUiContextAsync(string video)
    {
        var context = new RecordingContext();
        var previous = SynchronizationContext.Current;
        Task<VideoFrameService.VideoMetadata> reading;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            reading = VideoFrameService.ReadMetadataAsync(video, CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        await reading.WaitAsync(TimeSpan.FromSeconds(15));
        Check(context.Posts == 0, "Metadata processing does not resume on the UI context");
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
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
