using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LockscreenGif.Models;

namespace LockscreenGif.Services;

public static partial class VideoFrameService
{
    public static Task<VideoIndexingResult> IndexAsync(
        string input,
        double nominalFps,
        VideoDecodeOptions options,
        IProgress<int>? progress,
        CancellationToken cancellationToken,
        IProgress<VideoIndexProgress>? detailedProgress = null
    ) =>
        Task.Run(
            () =>
                IndexWithOptionsCoreAsync(
                    nominalFps,
                    options,
                    progress,
                    cancellationToken,
                    (onLine, output, token) =>
                        RunAsync(HardwareIndexArguments(input, options.AdapterIndex!.Value), onLine, token, output, 8),
                    token => IndexCoreAsync(input, nominalFps, progress, token, detailedProgress),
                    detailedProgress: detailedProgress
                ),
            cancellationToken
        );

    internal static string[] HardwareIndexArguments(string input, int adapterIndex) =>
        [
            "-xerror",
            "-err_detect",
            "explode",
            "-noautorotate",
            "-hwaccel",
            "d3d11va",
            "-hwaccel_device",
            adapterIndex.ToString(CultureInfo.InvariantCulture),
            "-hwaccel_output_format",
            "d3d11",
            "-i",
            input,
            "-map",
            "0:v:0",
            "-vf",
            "showinfo=checksum=0",
            "-an",
            "-sn",
            "-dn",
            "-fps_mode",
            "passthrough",
            "-progress",
            "pipe:1",
            "-stats_period",
            "60",
            "-f",
            "null",
            "-",
        ];

    // Both delegates complete only after their owned process and pipes have stopped.
    // This seam lets tests exercise failure/cancellation without requiring a GPU.
    internal static async Task<VideoIndexingResult> IndexWithOptionsCoreAsync(
        double nominalFps,
        VideoDecodeOptions options,
        IProgress<int>? progress,
        CancellationToken cancellationToken,
        Func<Action<string>, Stream, CancellationToken, Task> hardwareProcess,
        Func<CancellationToken, Task<VideoFrameIndex>> cpuIndex,
        TimeSpan? hardwareStallTimeout = null,
        IProgress<VideoIndexProgress>? detailedProgress = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var fallback = false;
        if (options.CanUseHardware)
        {
            try
            {
                var index = await IndexHardwareAsync(
                        nominalFps,
                        progress,
                        cancellationToken,
                        hardwareProcess,
                        hardwareStallTimeout ?? TimeSpan.FromSeconds(10),
                        detailedProgress
                    )
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new(index, VideoIndexDecoder.D3D11, false, timer.Elapsed);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsHardwareIndexFailure(ex))
            {
                fallback = true;
                Logger.Info($"Hardware video indexing failed ({ex.GetType().Name}); retrying with CPU decoding.");
            }
        }

        // Cancellation wins even if it arrived during GPU cleanup or failure logging.
        cancellationToken.ThrowIfCancellationRequested();
        if (fallback)
        {
            detailedProgress?.Report(new(0, null));
        }
        var cpu = await cpuIndex(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(cpu, VideoIndexDecoder.Cpu, fallback, timer.Elapsed);
    }

    private static bool IsHardwareIndexFailure(Exception exception) =>
        exception
            is MediaProcessingException
                or IOException
                or InvalidDataException
                or ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or TimeoutException;

    private static async Task<VideoFrameIndex> IndexHardwareAsync(
        double nominalFps,
        IProgress<int>? progress,
        CancellationToken cancellationToken,
        Func<Action<string>, Stream, CancellationToken, Task> runProcess,
        TimeSpan stallTimeout,
        IProgress<VideoIndexProgress>? detailedProgress
    )
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        watchdog.CancelAfter(stallTimeout);
        using var output = new MemoryStream();
        var timestamps = new List<double>();
        var timeBase = 0d;
        var lastDuration = 1 / nominalFps;
        var progressClock = Stopwatch.StartNew();
        var progressTracker = detailedProgress is null ? null : new IndexProgressTracker();
        try
        {
            await runProcess(
                    line =>
                    {
                        watchdog.Token.ThrowIfCancellationRequested();
                        progressTracker?.ReadHeader(line);
                        timeBase = ReadTimeBase(line, timeBase);
                        if (!line.Contains("showinfo", StringComparison.Ordinal) || !Regex.IsMatch(line, @"\bn:\s*\d+"))
                        {
                            return;
                        }
                        if (
                            timeBase <= 0
                            || !TryReadFrame(line, out var number, out var time, out var duration, timeBase)
                            || !Regex.IsMatch(line, @"\bfmt:d3d11(?:\s|$)")
                            || number != timestamps.Count
                            || !double.IsFinite(time)
                            || !double.IsFinite(duration)
                            || (timestamps.Count > 0 && time <= timestamps[^1])
                        )
                        {
                            throw new InvalidDataException("Hardware indexing returned invalid frame timing or non-D3D11 frames.");
                        }

                        timestamps.Add(time);
                        lastDuration = duration > 0 ? duration : 1 / nominalFps;
                        watchdog.CancelAfter(stallTimeout);
                        if (number == 0 || progressClock.ElapsedMilliseconds >= 200)
                        {
                            progress?.Report(number + 1);
                            detailedProgress?.Report(progressTracker!.Frame(number + 1, time, lastDuration));
                            progressClock.Restart();
                        }
                    },
                    output,
                    watchdog.Token
                )
                .ConfigureAwait(false);
            watchdog.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Hardware indexing stopped making frame progress.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        // Exit code alone cannot prove that FFmpeg finished the output. Also verify
        // its final progress marker and emitted count before accepting any GPU data.
        var completion = Encoding.UTF8.GetString(output.ToArray());
        var counts = Regex.Matches(completion, @"(?m)^frame=(\d+)\r?$");
        if (
            !completion.TrimEnd().EndsWith("progress=end", StringComparison.Ordinal)
            || counts.Count == 0
            || !int.TryParse(counts[^1].Groups[1].Value, out var count)
            || count != timestamps.Count
            || count == 0
        )
        {
            throw new InvalidDataException("Hardware indexing did not finish every decoded frame.");
        }
        var index = new VideoFrameIndex(timestamps, lastDuration);
        progress?.Report(index.Count);
        detailedProgress?.Report(new(index.Count, 1));
        return index;
    }
}
