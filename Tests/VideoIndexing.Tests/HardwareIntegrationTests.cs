using System.Diagnostics;
using System.Globalization;
using LockscreenGif.Models;
using LockscreenGif.Services;

internal static class HardwareIntegrationTests
{
    public static async Task RunAsync(string[] args)
    {
        var adapterArgument = Array.IndexOf(args, "--adapter");
        var adapter = adapterArgument >= 0 ? int.Parse(args[adapterArgument + 1], CultureInfo.InvariantCulture) : 0;
        var options = new VideoDecodeOptions(true, adapter);
        var root = TempDirectoryService.GetAppTempRoot();
        var hardwareSuccesses = 0;
        var h264Supported = false;
        try
        {
            var codecs = new (string Name, string[] Arguments)[]
            {
                ("h264", ["-c:v", "libx264", "-preset", "fast", "-bf", "3", "-x264-params", "b-adapt=0:scenecut=0"]),
                (
                    "hevc",
                    [
                        "-c:v",
                        "libx265",
                        "-preset",
                        "ultrafast",
                        "-x265-params",
                        "pools=none:frame-threads=1:bframes=3:b-adapt=0:scenecut=0:log-level=error",
                    ]
                ),
                ("vp9", ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8"]),
                ("av1", ["-c:v", "libaom-av1", "-cpu-used", "8"]),
                ("ffv1", ["-c:v", "ffv1"]),
            };
            foreach (var codec in codecs)
            {
                var path = Path.Combine(root, codec.Name + ".mkv");
                await CreateAsync(path, codec.Arguments);
                var result = await CompareAsync(path, 30000d / 1001, options, codec.Name);
                hardwareSuccesses += result.Decoder == VideoIndexDecoder.D3D11 ? 1 : 0;
                if (codec.Name == "h264")
                {
                    h264Supported = result.Decoder == VideoIndexDecoder.D3D11;
                }
                if (codec.Name == "ffv1")
                {
                    HardwareIndexingTests.Check(
                        result.HardwareFallbackUsed && result.Decoder == VideoIndexDecoder.Cpu,
                        "unsupported codec quietly falls back on real FFmpeg"
                    );
                }
            }

            var variable = Path.Combine(root, "h264-vfr.mkv");
            await CreateAsync(
                variable,
                new[] { "-vf", "setpts=if(lt(N\\,6)\\,N\\,6+(N-6)*3)", "-fps_mode", "passthrough" }.Concat(codecs[0].Arguments)
            );
            await CompareAsync(variable, 30000d / 1001, options, "VFR B-frames", h264Supported);
            var source = Path.Combine(root, "h264.mkv");
            var rotated = Path.Combine(root, "rotated.mp4");
            await VideoFrameService.RunAsync(
                ["-display_rotation:v:0", "90", "-i", source, "-c", "copy", rotated],
                null,
                CancellationToken.None
            );
            await CompareAsync(rotated, 30000d / 1001, options, "rotation", h264Supported);
            var offset = Path.Combine(root, "offset.mp4");
            await VideoFrameService.RunAsync(
                ["-i", rotated, "-c", "copy", "-output_ts_offset", "3600.1", offset],
                null,
                CancellationToken.None
            );
            await CompareAsync(offset, 30000d / 1001, options, "source offset", h264Supported);

            HardwareIndexingTests.Check(hardwareSuccesses > 0, "opt-in integration observes actual D3D11 indexing on at least one codec");
            var sourceArgument = Array.IndexOf(args, "--video");
            if (sourceArgument >= 0)
            {
                var video = args[sourceArgument + 1];
                var metadata = await VideoFrameService.ReadMetadataAsync(video, CancellationToken.None);
                await CompareAsync(video, metadata.Fps, options, "supplied video");
            }
        }
        finally
        {
            // Root is created by this harness and never accepts user-provided paths.
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task CreateAsync(string output, IEnumerable<string> encoding) =>
        VideoFrameService.RunAsync(
            new[] { "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30000/1001", "-frames:v", "24" }.Concat(encoding).Append(output),
            null,
            CancellationToken.None
        );

    private static async Task<VideoIndexingResult> CompareAsync(
        string path,
        double fps,
        VideoDecodeOptions options,
        string description,
        bool requireHardware = false
    )
    {
        var cpuTimer = Stopwatch.StartNew();
        var cpuProgress = new IndexProgressTests.RecordingProgress();
        var cpuResult = await VideoFrameService.IndexAsync(path, fps, new(false, null), null, CancellationToken.None, cpuProgress);
        var cpu = cpuResult.Index;
        cpuTimer.Stop();
        var selectedProgress = new IndexProgressTests.RecordingProgress();
        var result = await VideoFrameService.IndexAsync(path, fps, options, null, CancellationToken.None, selectedProgress);
        foreach (var progress in new[] { cpuProgress, selectedProgress })
        {
            HardwareIndexingTests.Check(
                progress.Values.Count >= 2
                    && progress.Values.Take(progress.Values.Count - 1).All(value => value.Fraction is null or >= 0 and < 1)
                    && progress.Values[^1] == new VideoIndexProgress(cpu.Count, 1),
                description + " emits indexing progress and completes only after successful decoding"
            );
            if (description is "h264" or "hevc" or "vp9" or "av1" or "VFR B-frames")
            {
                HardwareIndexingTests.Check(
                    progress.Values.Any(value => value.Fraction is > 0 and < 1),
                    description + " reports a percentage from the real decoder header"
                );
            }
        }
        if (result.HardwareFallbackUsed)
        {
            HardwareIndexingTests.Check(
                selectedProgress.Values.Contains(new(0, null)),
                description + " resets progress before CPU fallback"
            );
        }
        if (requireHardware)
        {
            HardwareIndexingTests.Check(result.Decoder == VideoIndexDecoder.D3D11, description + " retains hardware decoding");
        }
        HardwareIndexingTests.Check(
            cpu.Count == result.Index.Count
                && cpu.SourceStartTime == result.Index.SourceStartTime
                && Enumerable.Range(0, cpu.Count + 1).All(i => cpu.TimeAt(i) == result.Index.TimeAt(i)),
            description + " matches every CPU timestamp and final duration exactly"
        );
        Console.WriteLine(
            FormattableString.Invariant(
                $"BENCH {description}: CPU={cpuTimer.Elapsed.TotalMilliseconds:F1}ms selected={result.Duration.TotalMilliseconds:F1}ms decoder={result.Decoder} fallback={result.HardwareFallbackUsed} frames={cpu.Count}"
            )
        );
        return result;
    }
}
