using System.Globalization;
using LockscreenGif.Models;
using LockscreenGif.Services;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException("FAILED: " + message);
    }

    Console.WriteLine("PASS " + message);
}

static bool Close(double a, double b, double epsilon = 0.000001) => Math.Abs(a - b) < epsilon;

static byte[] Pixels(string path) => File.ReadAllBytes(path);

var root = TempDirectoryService.GetAppTempRoot();
try
{
    Check(VideoFrameIndex.TryParseTime("5.25", out var seconds) && seconds == 5.25, "plain seconds");
    Check(VideoFrameIndex.TryParseTime("1:05.25", out seconds) && seconds == 65.25, "minutes and seconds");
    Check(VideoFrameIndex.TryParseTime("1:02:03.125", out seconds) && seconds == 3723.125, "hour-long time entry");
    Check(VideoFrameIndex.FormatTime(3723.125) == "1:02:03.125", "hours never wrap");
    foreach (var invalid in new[] { "NaN", "Infinity", "-1", "1:60", "1:2:60", "", "1::2", "1:2:3:4" })
    {
        Check(!VideoFrameIndex.TryParseTime(invalid, out _), "reject invalid time " + invalid);
    }

    var index = new VideoFrameIndex([10, 10.04, 10.12, 10.16], 0.08);
    Check(index.Count == 4 && Close(index.Duration, 0.24), "VFR timestamps normalized without FPS approximation");
    Check(index.NearestBoundary(0.119) == 2 && index.FrameAt(0.119) == 1, "boundary snapping differs from displayed frame");
    Check(index.NearestBoundary(100) == 4 && index.NearestBoundary(-1) == 0, "boundary clamps");
    Check(index.PresentationTimes(1, 3).SequenceEqual(new[] { 0d, index.TimeAt(2) - index.TimeAt(1) }), "exclusive end");
    foreach (var range in new[] { (0, 0), (2, 1), (-1, 1), (0, 5) })
    {
        try
        {
            index.ValidateRange(range.Item1, range.Item2);
            throw new Exception("Invalid interval accepted");
        }
        catch (ArgumentOutOfRangeException) { }
    }
    Check(true, "empty, crossed, and out-of-bounds intervals rejected");
    Check(
        VideoFrameService.TryReadFrame(
            "n: 3 pts: 108003001 pts_time:3600.1 duration: 1001 duration_time:0.0333667",
            out var n,
            out var time,
            out var duration,
            1d / 30000
        )
            && n == 3
            && Close(time, 108003001d / 30000)
            && Close(duration, 1001d / 30000),
        "integer timestamps retain precision beyond one hour"
    );

    var cfr = Path.Combine(root, "fractional.mkv");
    await VideoFrameService.RunAsync(
        ["-f", "lavfi", "-i", "testsrc2=size=64x48:rate=30000/1001", "-frames:v", "12", "-c:v", "ffv1", cfr],
        null,
        CancellationToken.None
    );
    var cfrIndex = await VideoFrameService.IndexAsync(cfr, 30000d / 1001, null, CancellationToken.None);
    Check(cfrIndex.Count == 12, "fractional FPS indexed by decoded frames");
    await ResponsivenessTests.RunAsync(cfr, cfrIndex);
    var all = await VideoFrameService.ExportAsync(cfr, cfrIndex, 0, 12, 64, 0, _ => { });
    var clip = await VideoFrameService.ExportAsync(cfr, cfrIndex, 3, 7, 64, 0, _ => { });
    var allFiles = Directory.GetFiles(all.Directory, "*.png").Order().ToArray();
    var clipFiles = Directory.GetFiles(clip.Directory, "*.png").Order().ToArray();
    Check(clipFiles.Length == 4, "four selected frames produce exactly four PNGs");
    for (var i = 0; i < 4; i++)
    {
        Check(Pixels(clipFiles[i]).SequenceEqual(Pixels(allFiles[i + 3])), $"export frame {i} matches source frame {i + 3}");
    }

    var single = await VideoFrameService.ExportAsync(cfr, cfrIndex, 11, 12, 64, 0, _ => { });
    Check(
        single.Timestamps.Length == 1 && Pixels(Directory.GetFiles(single.Directory, "*.png").Single()).SequenceEqual(Pixels(allFiles[11])),
        "last source frame can be exported alone"
    );
    var window = await VideoFrameService.PreviewWindowAsync(cfr, 3, 7, CancellationToken.None);
    Check(window.Count == 4, "neighboring preview window has exact frame count");
    var preview = await VideoFrameService.PreviewAsync(cfr, 11, CancellationToken.None);
    var enlarged = await VideoFrameService.ExportAsync(cfr, cfrIndex, 11, 12, 560, 0, _ => { });
    Check(
        preview.SequenceEqual(Pixels(Directory.GetFiles(enlarged.Directory, "*.png").Single())),
        "exact still matches exported boundary frame"
    );
    Check(
        (await VideoFrameService.ThumbnailsAsync(cfr, cfrIndex, CancellationToken.None)).Count == 8,
        "thumbnail strip contains eight valid PNGs"
    );

    var twenty = Path.Combine(root, "twenty.mkv");
    await VideoFrameService.RunAsync(
        ["-f", "lavfi", "-i", "testsrc2=size=64x48:rate=20", "-frames:v", "20", "-c:v", "ffv1", twenty],
        null,
        CancellationToken.None
    );
    var twentyIndex = await VideoFrameService.IndexAsync(twenty, 20, null, CancellationToken.None);
    var fifteen = await VideoFrameService.ExportAsync(twenty, twentyIndex, 0, 20, 64, 15, _ => { });
    Check(
        fifteen.Timestamps.Length == 15 && Close(fifteen.Timestamps[^1], 0.95),
        "20-to-15 FPS reduction meets target without losing the end frame"
    );

    var vfr = Path.Combine(root, "variable.mkv");
    await VideoFrameService.RunAsync(
        [
            "-f",
            "lavfi",
            "-i",
            "testsrc2=size=64x48:rate=25",
            "-frames:v",
            "8",
            "-vf",
            "setpts=if(lt(N\\,3)\\,N*0.04\\,0.12+(N-3)*0.12)/TB",
            "-fps_mode",
            "passthrough",
            "-c:v",
            "ffv1",
            vfr,
        ],
        null,
        CancellationToken.None
    );
    var vfrIndex = await VideoFrameService.IndexAsync(vfr, 25, null, CancellationToken.None);
    Check(vfrIndex.Count == 8 && Close(vfrIndex.TimeAt(5) - vfrIndex.TimeAt(4), 0.12), "VFR indexing preserves unequal frame durations");
    var variable = await VideoFrameService.ExportAsync(vfr, vfrIndex, 1, 8, 64, 0, _ => { });
    Check(
        variable.Timestamps.Length == 7 && variable.Timestamps.Zip(vfrIndex.PresentationTimes(1, 8)).All(p => Close(p.First, p.Second)),
        "VFR export preserves presentation timing"
    );
    var reduced = await VideoFrameService.ExportAsync(vfr, vfrIndex, 1, 8, 64, 5, _ => { });
    Check(
        reduced.Timestamps.Length < 7 && reduced.Timestamps[0] == 0 && Close(reduced.Timestamps[^1], variable.Timestamps[^1]),
        "FPS reduction preserves first and last selected frames"
    );

    var reducedFiles = Directory.GetFiles(reduced.Directory, "*.png").Order().ToArray();
    var variableFiles = Directory.GetFiles(variable.Directory, "*.png").Order().ToArray();
    Check(
        Pixels(reducedFiles[0]).SequenceEqual(Pixels(variableFiles[0]))
            && Pixels(reducedFiles[^1]).SequenceEqual(Pixels(variableFiles[^1])),
        "reduced GIF frames retain the actual boundary images"
    );

    Console.WriteLine("RUN native GIF encoding and decoder round-trip");
    var gif = await GifSkiService.CreateGif(variable.Directory, _ => { }, variable.Timestamps, vfrIndex.TimeAt(8) - vfrIndex.TimeAt(1));
    MediaFailureTests.CheckLibraryLifetime();
    var gifIndex = await VideoFrameService.IndexAsync(gif, 25, null, CancellationToken.None);
    Check(
        Close(gifIndex.Duration, vfrIndex.TimeAt(8) - vfrIndex.TimeAt(1), 0.011),
        "encoded VFR GIF keeps clip duration within GIF timing resolution"
    );
    var singleGif = await GifSkiService.CreateGif(single.Directory, _ => { }, single.Timestamps, cfrIndex.TimeAt(12) - cfrIndex.TimeAt(11));
    Check(File.Exists(singleGif) && new FileInfo(singleGif).Length > 0, "single-frame GIF generation succeeds");
    await MetadataTests.RunAsync(cfr, vfr);
    await ResolutionTests.RunAsync();
    await MediaFailureTests.RunAsync(allFiles[0]);
    using var activeCancellation = new CancellationTokenSource();
    var cancelProgress = new CancelProgress(activeCancellation);
    try
    {
        await VideoFrameService.IndexAsync(cfr, 30, cancelProgress, activeCancellation.Token);
        throw new Exception("Active cancellation ignored");
    }
    catch (OperationCanceledException)
    {
        Check(true, "active indexing cancels and waits for decoder exit");
    }
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    try
    {
        await VideoFrameService.IndexAsync(cfr, 30, null, cts.Token);
        throw new Exception("Cancellation ignored");
    }
    catch (OperationCanceledException)
    {
        Check(true, "cancelled indexing stops without results");
    }
    Console.WriteLine("All video editing checks passed.");
}
finally
{
    // Only the unique, test-owned directory is removed.
    Directory.Delete(root, recursive: true);
}

sealed class CancelProgress(CancellationTokenSource source) : IProgress<int>
{
    public void Report(int value) => source.Cancel();
}
