using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using LockscreenGif.Models;

namespace LockscreenGif.Services;

/// <summary>Uses decoded presentation order for indexing, stills and export.</summary>
public static partial class VideoFrameService
{
    [GeneratedRegex(@"\bn:\s*(\d+)\s+pts:\s*(-?\d+)\s+pts_time:([^\s]+).*?duration:\s*(-?\d+)\s+duration_time:([^\s]+)")]
    private static partial Regex FrameInfo();

    public static bool TryReadFrame(string line, out int number, out double time, out double duration, double timeBase = 0)
    {
        number = 0;
        time = duration = 0;
        var match = FrameInfo().Match(line);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out number))
        {
            return false;
        }

        if (timeBase > 0)
        {
            if (!long.TryParse(match.Groups[2].Value, out var pts) || !long.TryParse(match.Groups[4].Value, out var ticks))
            {
                return false;
            }

            time = pts * timeBase;
            duration = ticks * timeBase;
            return true;
        }
        return double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out time)
            && double.TryParse(match.Groups[5].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
    }

    private static double ReadTimeBase(string line, double current)
    {
        var match = Regex.Match(line, @"config in time_base:\s*(\d+)/(\d+)");
        if (!match.Success)
        {
            return current;
        }

        var numerator = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var denominator = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return denominator > 0 ? numerator / denominator : current;
    }

    // Task.Run is deliberate: async pipe reads can complete synchronously, and the
    // final timestamp/PNG processing must never resume on a caller's UI context.
    public static Task<VideoFrameIndex> IndexAsync(
        string input,
        double nominalFps,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    ) => Task.Run(() => IndexCoreAsync(input, nominalFps, progress, cancellationToken), cancellationToken);

    public static Task<IReadOnlyList<byte[]>> PreviewWindowAsync(string input, int start, int end, CancellationToken cancellationToken) =>
        Task.Run(() => PreviewWindowCoreAsync(input, start, end, cancellationToken), cancellationToken);

    public static Task<IReadOnlyList<byte[]>> PreviewWindowAsync(
        string input,
        VideoFrameIndex index,
        int start,
        int end,
        CancellationToken cancellationToken
    ) => Task.Run(() => IndexedPreviewWindowCoreAsync(input, index, start, end, cancellationToken), cancellationToken);

    public static Task<IReadOnlyList<byte[]>> ThumbnailsAsync(string input, VideoFrameIndex index, CancellationToken cancellationToken) =>
        Task.Run(() => ThumbnailsCoreAsync(input, index, cancellationToken), cancellationToken);

    public static Task<(string Directory, double[] Timestamps)> ExportAsync(
        string input,
        VideoFrameIndex index,
        int start,
        int end,
        int width,
        double maximumFps,
        Action<double> progress
    ) => Task.Run(() => ExportCoreAsync(input, index, start, end, width, maximumFps, progress));

    private static async Task<VideoFrameIndex> IndexCoreAsync(
        string input,
        double nominalFps,
        IProgress<int>? progress,
        CancellationToken cancellationToken,
        IProgress<VideoIndexProgress>? detailedProgress = null
    )
    {
        var progressTracker = detailedProgress is null ? null : new IndexProgressTracker();
        var timeBase = 0d;
        var timestamps = new List<double>();
        var lastDuration = 1 / nominalFps;
        var progressClock = Stopwatch.StartNew();
        await RunAsync(
            [
                // Indexing needs frame timing, not rendered pixels. Keep every decoded
                // frame, but avoid deblocking and rotating images that are discarded.
                "-skip_loop_filter",
                "all",
                "-noautorotate",
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
                "-f",
                "null",
                "-",
            ],
            line =>
            {
                progressTracker?.ReadHeader(line);
                timeBase = ReadTimeBase(line, timeBase);
                if (!TryReadFrame(line, out var number, out var time, out var duration, timeBase))
                {
                    return;
                }

                if (number != timestamps.Count)
                {
                    throw new InvalidDataException("Unexpected decoded frame order.");
                }

                timestamps.Add(time);
                lastDuration = duration > 0 ? duration : 1 / nominalFps;
                if (number == 0 || progressClock.ElapsedMilliseconds >= 200)
                {
                    progress?.Report(number + 1);
                    detailedProgress?.Report(progressTracker!.Frame(number + 1, time, lastDuration));
                    progressClock.Restart();
                }
            },
            cancellationToken,
            maximumDecoderThreads: 8
        );
        cancellationToken.ThrowIfCancellationRequested();
        var index = new VideoFrameIndex(timestamps, lastDuration);
        progress?.Report(timestamps.Count);
        detailedProgress?.Report(new(index.Count, 1));
        return index;
    }

    public static async Task<byte[]> PreviewAsync(string input, int frame, CancellationToken cancellationToken) =>
        (await PreviewWindowAsync(input, frame, frame + 1, cancellationToken)).Single();

    private static async Task<IReadOnlyList<byte[]>> PreviewWindowCoreAsync(
        string input,
        int start,
        int end,
        CancellationToken cancellationToken
    )
    {
        if (start < 0 || end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        using var output = new MemoryStream();
        await RunAsync(
            [
                "-i",
                input,
                "-map",
                "0:v:0",
                "-vf",
                $"trim=start_frame={start}:end_frame={end},scale=560:-2",
                "-frames:v",
                (end - start).ToString(CultureInfo.InvariantCulture),
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
            cancellationToken,
            output
        );
        var images = SplitPngs(output.ToArray());
        if (images.Count != end - start)
        {
            throw new InvalidDataException("The selected frames could not be decoded.");
        }

        return images;
    }

    private static async Task<IReadOnlyList<byte[]>> ThumbnailsCoreAsync(
        string input,
        VideoFrameIndex index,
        CancellationToken cancellationToken
    )
    {
        var frames = Enumerable.Range(0, 8).Select(i => index.FrameAt(index.Duration * i / 8)).Distinct().ToArray();
        // Process startup dominates small clips. For longer videos, decode only
        // the GOP around each sample instead of scanning almost the whole file again.
        if (frames[^1] < 512 || index.SourceStartTime < 0)
        {
            return await SequentialThumbnailsAsync(input, frames, cancellationToken).ConfigureAwait(false);
        }

        var images = new byte[frames.Length][];
        try
        {
            await Parallel
                .ForEachAsync(
                    Enumerable.Range(0, frames.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
                    async (position, token) =>
                    {
                        images[position] = await SeekThumbnailAsync(input, index, frames[position], token).ConfigureAwait(false);
                    }
                )
                .ConfigureAwait(false);
            return images;
        }
        catch (Exception ex) when (ex is MediaProcessingException or InvalidDataException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Some inputs cannot seek reliably. Keep the original exact-frame path
            // as a fallback, after all seek workers and their processes have exited.
            Logger.Info("Timeline thumbnail seeking was unavailable; using sequential extraction.");
            return await SequentialThumbnailsAsync(input, frames, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<byte[]> SeekThumbnailAsync(
        string input,
        VideoFrameIndex index,
        int frame,
        CancellationToken cancellationToken
    ) => (await SeekImagesAsync(input, index, frame, frame + 1, 160, cancellationToken).ConfigureAwait(false)).Single();

    private static async Task<IReadOnlyList<byte[]>> IndexedPreviewWindowCoreAsync(
        string input,
        VideoFrameIndex index,
        int start,
        int end,
        CancellationToken cancellationToken
    )
    {
        index.ValidateRange(start, end);
        if (index.SourceStartTime >= 0)
        {
            try
            {
                return await SeekImagesAsync(input, index, start, end, 560, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MediaProcessingException or InvalidDataException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Logger.Info("Indexed frame preview seeking was unavailable; using sequential extraction.");
            }
        }

        return await PreviewWindowCoreAsync(input, start, end, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<byte[]>> SeekImagesAsync(
        string input,
        VideoFrameIndex index,
        int start,
        int end,
        int width,
        CancellationToken cancellationToken
    )
    {
        index.ValidateRange(start, end);
        var target = index.SourceStartTime + index.TimeAt(start);
        var precedingGap = start > 0 ? index.TimeAt(start) - index.TimeAt(start - 1) : 0;
        // Seek inside the gap before the desired frame. Converting its exact PTS
        // to FFmpeg's microsecond seek clock could otherwise round past that frame.
        var seek = Math.Round(target - precedingGap / 4, 6);
        var timeBase = 0d;
        var decodedTimes = new List<double>();
        using var output = new MemoryStream();
        await RunAsync(
                (string[])
                    [
                        "-ss",
                        seek.ToString("F6", CultureInfo.InvariantCulture),
                        "-i",
                        input,
                        "-map",
                        "0:v:0",
                        "-vf",
                        $"showinfo=checksum=0,scale={width}:-2",
                        "-frames:v",
                        (end - start).ToString(CultureInfo.InvariantCulture),
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
                line =>
                {
                    timeBase = ReadTimeBase(line, timeBase);
                    if (TryReadFrame(line, out var number, out var time, out _, timeBase) && number < end - start)
                    {
                        if (number != decodedTimes.Count)
                        {
                            throw new InvalidDataException("Unexpected sought frame order.");
                        }

                        decodedTimes.Add(seek + time);
                    }
                },
                cancellationToken,
                output
            )
            .ConfigureAwait(false);
        var images = SplitPngs(output.ToArray());
        if (images.Count != end - start || decodedTimes.Count != images.Count)
        {
            throw new InvalidDataException("The frame seek did not return the requested image count.");
        }

        for (var i = 0; i < images.Count; i++)
        {
            var frame = start + i;
            var followingGap = index.TimeAt(frame + 1) - index.TimeAt(frame);
            var tolerance = (frame > 0 ? Math.Min(index.TimeAt(frame) - index.TimeAt(frame - 1), followingGap) : followingGap) / 2;
            var expectedTime = index.SourceStartTime + index.TimeAt(frame);
            if (!double.IsFinite(decodedTimes[i]) || Math.Abs(decodedTimes[i] - expectedTime) >= tolerance)
            {
                throw new InvalidDataException("The frame seek did not return the indexed frames.");
            }
        }

        return images;
    }

    private static async Task<IReadOnlyList<byte[]>> SequentialThumbnailsAsync(
        string input,
        int[] frames,
        CancellationToken cancellationToken
    )
    {
        var select = string.Join('+', frames.Select(n => $"eq(n\\,{n})"));
        using var output = new MemoryStream();
        await RunAsync(
            [
                "-i",
                input,
                "-map",
                "0:v:0",
                "-vf",
                $"select={select},scale=160:-2",
                "-frames:v",
                frames.Length.ToString(CultureInfo.InvariantCulture),
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
            cancellationToken,
            output
        );
        var images = SplitPngs(output.ToArray());
        if (images.Count != frames.Length)
        {
            throw new InvalidDataException("The timeline thumbnails could not be decoded.");
        }

        return images;
    }

    private static IReadOnlyList<byte[]> SplitPngs(byte[] data)
    {
        var images = new List<byte[]>();
        var offset = 0;
        while (offset < data.Length)
        {
            var start = offset;
            if (offset + 8 > data.Length || !data.AsSpan(offset, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                throw new InvalidDataException("Invalid preview image.");
            }

            offset += 8;
            while (true)
            {
                if (offset + 12 > data.Length)
                {
                    throw new InvalidDataException("Incomplete preview image.");
                }

                var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
                if (length < 0 || length > data.Length - offset - 12)
                {
                    throw new InvalidDataException("Invalid PNG chunk.");
                }

                var end = data.AsSpan(offset + 4, 4).SequenceEqual("IEND"u8);
                offset += length + 12;
                if (end)
                {
                    break;
                }
            }
            images.Add(data[start..offset]);
        }
        return images;
    }

    public static string ExportFilter(int start, int end, int width, double maximumFps)
    {
        if (start < 0 || end <= start || width <= 0 || !double.IsFinite(maximumFps) || maximumFps < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        var filter = $"trim=start_frame={start}:end_frame={end},setpts=PTS-STARTPTS";
        if (maximumFps > 0)
        {
            var rate = maximumFps.ToString("R", CultureInfo.InvariantCulture);
            // Select one source frame per target-time bucket. Comparing only time since
            // the previous frame would turn 20 -> 15 fps into 10 fps.
            filter += $@",select=eq(n\,0)+gt(floor(t*{rate}+0.000001)\,floor(prev_selected_t*{rate}+0.000001))+eq(n\,{end - start - 1})";
        }
        return filter + $",showinfo=checksum=0,scale={width}:-2";
    }

    private static async Task<(string Directory, double[] Timestamps)> ExportCoreAsync(
        string input,
        VideoFrameIndex index,
        int start,
        int end,
        int width,
        double maximumFps,
        Action<double> progress
    )
    {
        index.ValidateRange(start, end);
        var directory = FfmpegService.CreateTempDirectory();
        var timeBase = 0d;
        var times = new List<double>();
        var duration = index.TimeAt(end) - index.TimeAt(start);
        await RunAsync(
            [
                "-i",
                input,
                "-map",
                "0:v:0",
                "-vf",
                ExportFilter(start, end, width, maximumFps),
                "-frames:v",
                (end - start).ToString(CultureInfo.InvariantCulture),
                "-an",
                "-sn",
                "-dn",
                "-fps_mode",
                "passthrough",
                "-c:v",
                "png",
                Path.Combine(directory, "frame_%06d.png"),
            ],
            line =>
            {
                timeBase = ReadTimeBase(line, timeBase);
                if (!TryReadFrame(line, out _, out var time, out _, timeBase))
                {
                    return;
                }

                times.Add(time);
                progress(Math.Clamp(time / duration * 100, 0, 100));
            },
            CancellationToken.None
        );
        if (times.Count == 0)
        {
            throw new InvalidDataException("No frames were extracted.");
        }

        progress(100);
        return (directory, times.ToArray());
    }

    internal static async Task RunAsync(
        IEnumerable<string> arguments,
        Action<string>? onLine,
        CancellationToken cancellationToken,
        Stream? output = null,
        int maximumDecoderThreads = 4
    )
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Vendor", "FFMPEG", "ffmpeg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        // Cap decoder workers at half the logical processors (at least one).
        // The single initial timing pass can use more decoder threads; simultaneous
        // thumbnail/still decodes and image encoding retain their smaller caps.
        var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4).ToString(CultureInfo.InvariantCulture);
        var decoderThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, maximumDecoderThreads).ToString(CultureInfo.InvariantCulture);
        var args = arguments.ToList();
        args.InsertRange(args.Count - 1, ["-threads", threads]); // Output encoder.
        foreach (
            var arg in new[]
            {
                "-hide_banner",
                "-nostdin",
                "-nostats",
                "-y",
                "-threads",
                decoderThreads,
                "-filter_threads",
                "2",
                "-filter_complex_threads",
                "2",
            }.Concat(args)
        )
        {
            info.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = info };
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        using var registration = cancellationToken.Register(() => StopDecoder(process));
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output ?? Stream.Null);
        var recent = new Queue<string>();
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                recent.Enqueue(line);
                if (recent.Count > 12)
                {
                    recent.Dequeue();
                }

                onLine?.Invoke(line);
            }
            await copy.ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                throw new MediaProcessingException(
                    MediaProcessingComponent.Ffmpeg,
                    process.ExitCode,
                    "Video decoding failed: " + string.Join(Environment.NewLine, recent)
                );
            }
        }
        finally
        {
            StopDecoder(process);
            await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await copy.ConfigureAwait(false);
        }
    }

    private static void StopDecoder(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
