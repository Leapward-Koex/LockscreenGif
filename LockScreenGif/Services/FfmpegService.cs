using System.Reflection;
using FFMpegCore;
using FFMpegCore.Enums;

namespace LockscreenGif.Services;
public class FfmpegService
{
    private static readonly List<string> _tracked = [];
    private static readonly string _exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
    private const string _prefix = ".ffmpeg_temp_";

    public static string CreateTempDirectory()
    {
        var dir = Path.Combine(_exeDir, $"{_prefix}{Guid.NewGuid()}");
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
        foreach (var dir in Directory.EnumerateDirectories(_exeDir, $"{_prefix}*"))
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
        catch (IOException) { /* in‐use → ignore/log */ }
        catch (UnauthorizedAccessException) { /* perms → ignore/log */ }
        catch (Exception ex)
        {
            Logger.Error($"Failed to delete {path}", ex);
        }
    }

    public static async Task<string> ApplyFastStartAsync(
        string inputFile)
    {
        // point to your bundled ffmpeg.exe
        GlobalFFOptions.Configure(new FFOptions
        {
            BinaryFolder = Path.Combine(AppContext.BaseDirectory, "Vendor", "FFMPEG")
        });

        var trimmedVideoDirectory = CreateTempDirectory();
        var outputFile = Path.Combine(trimmedVideoDirectory, "fastStart.mp4");

        await FFMpegArguments
            .FromFileInput(inputFile)
            .OutputToFile(outputFile, overwrite: true, opts => opts
                .WithFastStart()    // sets -movflags +faststart :contentReference[oaicite:0]{index=0}
            )
            .ProcessAsynchronously();
        return outputFile;
    }

    public static async Task<string> TrimVideoAsync(
        string inputFile,
        TimeSpan startTime,
        TimeSpan endTime)
    {
        Logger.Info($"Going to trim video {inputFile} from {startTime.TotalSeconds}s to {endTime.TotalSeconds}s");
        // point FFMpegCore at your bundled ffmpeg.exe
        GlobalFFOptions.Configure(new FFOptions
        {
            BinaryFolder = Path.Combine(AppContext.BaseDirectory, "Vendor", "FFMPEG")
        });

        var clipDuration = endTime - startTime;

        var trimmedVideoDirectory = CreateTempDirectory();
        var outputFile = Path.Combine(trimmedVideoDirectory, "trimmed.mp4");
        var result = await FFMpegArguments
            .FromFileInput(inputFile)
            .OutputToFile(outputFile, overwrite: true, options => options
                .Seek(startTime)
                .WithDuration(clipDuration)
            )
            .ProcessAsynchronously();
        if (!result)
        {
            throw new Exception("Failed to trim video");
        }

        return outputFile;
    }

    public static async Task<string> ExtractPngFramesAsync(
        string videoInput,
        Action<double> onPercentageProgress,
        int width,
        double fps,
        TimeSpan videoDuration)
    {
        Logger.Info($"Going to extract pngs from {videoInput}. Width {width}, fps {fps}, duration {videoDuration.TotalSeconds}");

        // ensure ffmpeg.exe is picked up from your Vendor folder
        GlobalFFOptions.Configure(new FFOptions
        {
            BinaryFolder = Path.Combine(AppContext.BaseDirectory, "Vendor", "FFMPEG")
        });
        var framesDirectory = CreateTempDirectory();

        var outputPattern = Path.Combine(framesDirectory, "frame_%06d.png");

        var result = await FFMpegArguments
          .FromFileInput(videoInput)
          .OutputToFile(outputPattern, overwrite: true, options => options
            .WithVideoFilters(f => f
              .Scale(width, -2)                // scale to [width], keep aspect
            )
            .WithFramerate(fps)                // emit frames at “fps” per second :contentReference[oaicite:0]{index=0}
            .WithVideoCodec(VideoCodec.Image.Png)    // PNG sequence
          )
          .NotifyOnProgress(onPercentageProgress, videoDuration)
          .ProcessAsynchronously();
        if (result != true)
        {
            throw new Exception("Failed to extract pngs from video");
        }
        return framesDirectory;
    }
}
