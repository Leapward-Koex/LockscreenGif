using System.Text;
using LockscreenGif.Models;
using LockscreenGif.Services;

internal static class HardwareIndexingTests
{
    private static readonly VideoFrameIndex CpuIndex = new([0, 0.04, 0.08], 0.04);
    private static readonly VideoDecodeOptions Hardware = new(true, 2);

    public static async Task RunAsync()
    {
        foreach (
            var options in new[] { new VideoDecodeOptions(false, 2), new VideoDecodeOptions(true, null), new VideoDecodeOptions(true, -1) }
        )
        {
            var result = await RunAsync(options, (_, _, _) => throw new Exception("GPU must not start"));
            Check(result.Decoder == VideoIndexDecoder.Cpu && !result.HardwareFallbackUsed, "saved off or unavailable uses CPU immediately");
        }
        var arguments = VideoFrameService.HardwareIndexArguments("input.mkv", 2);
        Check(
            arguments.Contains("-noautorotate") && arguments.Contains("-xerror") && arguments.Contains("d3d11"),
            "hardware args preserve GPU surfaces and reject decoding errors"
        );
        Check(arguments[Array.IndexOf(arguments, "-hwaccel_device") + 1] == "2", "selected DXGI adapter is passed to FFmpeg");

        var success = await RunAsync(Hardware, CompleteAsync);
        Check(success.Decoder == VideoIndexDecoder.D3D11 && !success.HardwareFallbackUsed, "actual hardware frames are accepted");
        Check(
            success.Index.Count == 3 && success.Index.SourceStartTime == 10 && Math.Abs(success.Index.Duration - 0.12) < 1e-9,
            "integer timing, source offset and final duration retained"
        );
        Check(success.Duration > TimeSpan.Zero, "indexing result measures complete operation");

        await FallbackAsync((_, _, _) => throw new System.ComponentModel.Win32Exception(2), "launch failure");
        await FallbackAsync(
            (_, _, _) => throw new MediaProcessingException(MediaProcessingComponent.Ffmpeg, 1, "decode failed"),
            "initialization failure"
        );
        await FallbackAsync(
            async (line, output, _) =>
            {
                Emit(line, format: "yuv420p");
                await FinishAsync(output);
            },
            "silent software decoding"
        );
        await FallbackAsync(
            (line, _, _) =>
            {
                Emit(line);
                throw new MediaProcessingException(MediaProcessingComponent.Ffmpeg, 1, "failed after frames");
            },
            "mid-index failure discards partial timestamps"
        );
        await FallbackAsync(
            async (line, output, _) =>
            {
                Emit(line);
                await output.WriteAsync(Encoding.UTF8.GetBytes("frame=3\nprogress=continue\n"));
            },
            "missing completion marker"
        );
        await FallbackAsync(
            async (line, output, _) =>
            {
                Emit(line);
                await FinishAsync(output, 4);
            },
            "incomplete output count"
        );
        await FallbackAsync(
            async (line, output, _) =>
            {
                Emit(line, duplicate: true);
                await FinishAsync(output);
            },
            "out-of-order frame"
        );
        await FallbackAsync(async (_, output, _) => await FinishAsync(output, 0), "empty hardware decode");

        var drained = false;
        var watchdog = await VideoFrameService.IndexWithOptionsCoreAsync(
            25,
            Hardware,
            null,
            CancellationToken.None,
            async (line, _, token) =>
            {
                try
                {
                    Emit(line);
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    await Task.Delay(15);
                    drained = true;
                }
            },
            _ =>
            {
                Check(drained, "GPU cleanup completes before CPU retry");
                return Task.FromResult(CpuIndex);
            },
            TimeSpan.FromMilliseconds(40)
        );
        Check(
            watchdog.HardwareFallbackUsed && watchdog.Duration.TotalMilliseconds >= 40,
            "no-frame-progress timeout falls back and includes failure cost"
        );

        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        try
        {
            await VideoFrameService.IndexWithOptionsCoreAsync(
                25,
                Hardware,
                null,
                cancellation.Token,
                async (line, _, token) =>
                {
                    Emit(line);
                    cancellation.Cancel();
                    await Task.Delay(Timeout.Infinite, token);
                },
                _ =>
                {
                    attempts++;
                    return Task.FromResult(CpuIndex);
                }
            );
            throw new Exception("Cancellation was swallowed");
        }
        catch (OperationCanceledException) { }
        Check(attempts == 0, "cancellation never starts CPU fallback");

        var cpuFailure = new IOException("CPU failed too");
        try
        {
            await VideoFrameService.IndexWithOptionsCoreAsync(
                25,
                Hardware,
                null,
                CancellationToken.None,
                (_, _, _) => throw new InvalidDataException("GPU failed"),
                _ => throw cpuFailure
            );
            throw new Exception("CPU error was swallowed");
        }
        catch (IOException ex) when (ReferenceEquals(ex, cpuFailure)) { }
        Check(true, "CPU failure propagates to existing load-error handling");
    }

    private static Task<VideoIndexingResult> RunAsync(
        VideoDecodeOptions options,
        Func<Action<string>, Stream, CancellationToken, Task> hardware
    ) => VideoFrameService.IndexWithOptionsCoreAsync(25, options, null, CancellationToken.None, hardware, _ => Task.FromResult(CpuIndex));

    private static async Task FallbackAsync(Func<Action<string>, Stream, CancellationToken, Task> hardware, string description)
    {
        var result = await RunAsync(Hardware, hardware);
        Check(
            result.Decoder == VideoIndexDecoder.Cpu && result.HardwareFallbackUsed && ReferenceEquals(result.Index, CpuIndex),
            description
        );
    }

    private static async Task CompleteAsync(Action<string> line, Stream output, CancellationToken token)
    {
        Emit(line);
        await FinishAsync(output);
    }

    private static void Emit(Action<string> line, string format = "d3d11", bool duplicate = false)
    {
        line("[Parsed_showinfo_0] config in time_base: 1/1000, frame_rate: 25/1");
        for (var i = 0; i < 3; i++)
        {
            line(
                $"[Parsed_showinfo_0] n: {(duplicate ? 0 : i)} pts: {10000 + i * 40} pts_time:10 duration: 40 duration_time:0.04 fmt:{format} s:320x180"
            );
        }
    }

    private static Task FinishAsync(Stream output, int count = 3) =>
        output.WriteAsync(Encoding.UTF8.GetBytes($"frame={count}\nprogress=end\n")).AsTask();

    internal static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("FAILED: " + message);
        }
        Console.WriteLine("PASS " + message);
    }
}
