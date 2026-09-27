using System.Buffers.Binary;
using GifskiNet;

namespace LockscreenGif.Services;

public class GifSkiService
{
    private static readonly List<string> _tracked = [];
    private static readonly string _tempRoot = TempDirectoryService.GetAppTempRoot();
    private const string _prefix = "gifski_temp_";

    public static string CreateTempDirectory()
    {
        var dir = Path.Combine(_tempRoot, $"{_prefix}{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        _tracked.Add(dir);
        return dir;
    }

    public static void CleanupTempDirectories()
    {
        // delete the ones we deliberately created this session
        foreach (var dir in _tracked)
        {
            TryDelete(dir);
        }

        _tracked.Clear();

        // delete any orphaned folders from previous runs
        foreach (var dir in Directory.EnumerateDirectories(_tempRoot, $"{_prefix}*"))
        {
            TryDelete(dir);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Logger.Info($"Trying to delete {path}");
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
                Logger.Info($"Deleted {path}");
            }
        }
        catch (IOException)
        { /* in‐use → ignore/log */
        }
        catch (UnauthorizedAccessException)
        { /* perms → ignore/log */
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to delete {path}", ex);
        }
    }

    public static async Task<string> CreateGif(
        string inputDirectory,
        Action<double> onPercentageProgress,
        IReadOnlyList<double> presentationTimes,
        double duration
    )
    {
        Logger.Info($"Going to create gif with {inputDirectory}, duration {duration}");

        return await Task.Run(() =>
        {
            var frames = Directory
                .EnumerateFiles(inputDirectory, "frame_*.png")
                .Select(path =>
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    // e.g. name = "frame_000123"
                    var numPart = name.Substring(name.LastIndexOf('_') + 1);
                    return new { Path = path, Index = int.TryParse(numPart, out var n) ? n : 0 };
                })
                .OrderBy(x => x.Index)
                .ToArray();

            if (frames.Length == 0)
            {
                throw new InvalidOperationException("No frames found in " + inputDirectory);
            }

            if (frames.Length != presentationTimes.Count || duration <= presentationTimes[^1])
            {
                throw new InvalidDataException("Extracted frame timing does not match the selected clip.");
            }

            var (width, height) = ReadPngDimensions(frames[0].Path);
            var gifskiDll = Path.Combine(AppContext.BaseDirectory, "Vendor", "gifski", "gifski.dll");
            using var gifski = Gifski.Create(
                gifskiDll,
                settings =>
                {
                    // FFmpeg has already applied the selected resolution. Gifski's
                    // unspecified dimensions automatically shrink large images.
                    settings.Width = width;
                    settings.Height = height;
                    settings.Quality = 100;
                    settings.Extra = true;
                }
            );

            var outputFile = Path.Combine(CreateTempDirectory(), "output.gif");
            gifski.SetFileOutput(outputFile);

            // Gifski uses a positive first timestamp as the final frame delay.
            // Offset every PTS equally to retain all intermediate (including VFR) delays.
            var finalDelay = duration - presentationTimes[^1];

            for (var i = 0; i < frames.Length; i++)
            {
                var timestamp = presentationTimes[i] + finalDelay;
                gifski.AddFramePngFile(frameNumber: (uint)i, presentationTimestamp: timestamp, filePath: frames[i].Path);
                onPercentageProgress(((double)i / frames.Length) * 100);
            }

            var err = gifski.Finish();
            if (err != GifskiError.OK)
            {
                throw new Exception($"Gifski failed: {err}");
            }

            return outputFile;
        });
    }

    private static (uint Width, uint Height) ReadPngDimensions(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using var stream = File.OpenRead(path);
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || !header[12..16].SequenceEqual("IHDR"u8))
        {
            throw new InvalidDataException("The exported frame is not a PNG image.");
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        if (width is 0 or > ushort.MaxValue || height is 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException("The exported frame dimensions cannot be represented by a GIF.");
        }

        return (width, height);
    }
}
