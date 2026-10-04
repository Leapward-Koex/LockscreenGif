using System.Text;
using LockscreenGif.Models;
using LockscreenGif.Services;

internal static class IndexProgressTests
{
    public static async Task RunAsync()
    {
        var tracker = Tracker("  Duration: 00:00:10.00, start: 0.000000, bitrate: 200 kb/s");
        Check(tracker.Frame(1, 0, 0.04).Fraction == 0.004, "first frame reports time-based progress");
        Check(tracker.Frame(2, 4, 1).Fraction == 0.5, "variable frame durations use media time rather than nominal frame count");
        Check(tracker.Frame(3, 20, 0.04).Fraction == 0.99, "estimated duration cannot report completion before decoding finishes");

        var delayed = Tracker("  Duration: 00:00:20.00, start: 0.000000, bitrate: 200 kb/s");
        Check(delayed.Frame(1, 10, 1).Fraction == 0.1, "delayed video stream excludes its initial timestamp from duration");
        Check(delayed.Frame(2, 14, 1).Fraction == 0.5, "delayed video progress uses the remaining video timeline");

        foreach (
            var header in new[]
            {
                "  Duration: N/A, start: 0.000000, bitrate: N/A",
                "  Duration: 00:00:00.00, start: 0.000000, bitrate: 200 kb/s",
                "  Duration: 01:00:00.60, start: 3600.100000, bitrate: 200 kb/s",
                "  Duration: 00:00:00.60, start: 3600.100000, bitrate: 200 kb/s",
                "  Duration: 00:00:10.00, start: -5.000000, bitrate: 200 kb/s",
                "  Duration: 00:00:10.00, bitrate: 200 kb/s",
                "  Duration: broken, start: 0.000000, bitrate: 200 kb/s",
                "",
            }
        )
        {
            var unknown = Tracker(header).Frame(20, 0, 0.04);
            Check(unknown.FrameCount == 20 && unknown.Fraction is null, "unknown or ambiguous container duration retains frame count only");
        }
        Check(
            Tracker("  Duration: 00:00:00.01, start: 0.000000,").Frame(1, 0.02, 0.04).Fraction is null,
            "duration before first frame is not estimated"
        );
        Check(
            Tracker("  Duration: 00:00:10.00, start: 0.000000,").Frame(1, -1, 0.04).Fraction is null,
            "negative first timestamp is not estimated"
        );

        var progress = new RecordingProgress();
        await VideoFrameService.IndexWithOptionsCoreAsync(
            25,
            new(true, 0),
            null,
            CancellationToken.None,
            async (line, output, _) =>
            {
                EmitFrame(line);
                await output.WriteAsync(Encoding.UTF8.GetBytes("frame=1\nprogress=end\n"));
            },
            _ => throw new Exception("Unexpected CPU retry"),
            detailedProgress: progress
        );
        Check(
            progress.Values.Count == 2 && progress.Values[0].Fraction is > 0 and < 1 && progress.Values[^1] == new VideoIndexProgress(1, 1),
            "validated hardware completion reports 100 percent after in-flight progress"
        );

        progress = new RecordingProgress();
        var cpuCalled = false;
        await VideoFrameService.IndexWithOptionsCoreAsync(
            25,
            new(true, 0),
            null,
            CancellationToken.None,
            (line, _, _) =>
            {
                EmitFrame(line);
                throw new InvalidDataException("Hardware frame validation failed");
            },
            _ =>
            {
                Check(progress.Values[^1] == new VideoIndexProgress(0, null), "CPU retry resets failed hardware progress before it starts");
                cpuCalled = true;
                return Task.FromResult(new VideoFrameIndex([0, 0.04], 0.04));
            },
            detailedProgress: progress
        );
        Check(cpuCalled && progress.Values.All(value => value.Fraction != 1), "failed hardware attempt never reports completed progress");

        progress = new RecordingProgress();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await VideoFrameService.IndexWithOptionsCoreAsync(
                25,
                new(true, 0),
                null,
                cancellation.Token,
                (line, _, _) =>
                {
                    EmitFrame(line);
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                },
                _ => throw new Exception("Cancellation started a CPU retry"),
                detailedProgress: progress
            );
            throw new Exception("Cancellation was swallowed");
        }
        catch (OperationCanceledException) { }
        Check(
            progress.Values.Count == 1 && progress.Values[0].Fraction is > 0 and < 1,
            "cancelled hardware attempt reports neither a retry reset nor completion"
        );
    }

    private static VideoFrameService.IndexProgressTracker Tracker(string header)
    {
        var tracker = new VideoFrameService.IndexProgressTracker();
        tracker.ReadHeader(header);
        return tracker;
    }

    private static void EmitFrame(Action<string> line)
    {
        line("  Duration: 00:00:10.00, start: 0.000000, bitrate: 200 kb/s");
        line("[Parsed_showinfo_0] config in time_base: 1/1000, frame_rate: 25/1");
        line("[Parsed_showinfo_0] n: 0 pts: 0 pts_time:0 duration: 40 duration_time:0.04 fmt:d3d11 s:320x180");
    }

    internal sealed class RecordingProgress : IProgress<VideoIndexProgress>
    {
        public List<VideoIndexProgress> Values { get; } = [];

        public void Report(VideoIndexProgress value) => Values.Add(value);
    }

    private static void Check(bool condition, string message) => HardwareIndexingTests.Check(condition, message);
}
