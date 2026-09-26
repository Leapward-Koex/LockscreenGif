using System.Reflection;
using FFMpegCore;
using FFMpegCore.Enums;

namespace LockscreenGif.Services;

public class FfmpegService
{
    private static readonly List<string> _tracked = [];
    private static readonly string _tempRoot = TempDirectoryService.GetAppTempRoot();
    private const string _prefix = "ffmpeg_temp_";

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

    public static async Task<string> ApplyFastStartAsync(string inputFile)
    {
        // point to your bundled ffmpeg.exe
        GlobalFFOptions.Configure(new FFOptions { BinaryFolder = Path.Combine(AppContext.BaseDirectory, "Vendor", "FFMPEG") });

        var trimmedVideoDirectory = CreateTempDirectory();
        var outputFile = Path.Combine(trimmedVideoDirectory, "fastStart.mp4");

        await FFMpegArguments
            .FromFileInput(inputFile)
            .OutputToFile(
                outputFile,
                overwrite: true,
                opts => opts.WithFastStart() // sets -movflags +faststart :contentReference[oaicite:0]{index=0}
            )
            .ProcessAsynchronously();
        return outputFile;
    }
}
