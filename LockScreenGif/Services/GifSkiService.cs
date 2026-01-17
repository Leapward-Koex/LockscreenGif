using System.Diagnostics;
using System.Reflection;
using FFMpegCore;
using FFMpegCore.Enums;
using GifskiNet;
using WindowsDisplayAPI;

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
        catch (IOException) { /* in‐use → ignore/log */ }
        catch (UnauthorizedAccessException) { /* perms → ignore/log */ }
        catch (Exception ex)
        {
            Logger.Error($"Failed to delete {path}", ex);
        }
    }

    public static async Task<string> CreateGif(
        string inputDirectory,
        Action<double> onPercentageProgress,
        double frameRate)
    {
        Logger.Info($"Going to create gif with {inputDirectory}, framerate {frameRate}");

        return await Task.Run(() =>
        {
            var baseDir = AppContext.BaseDirectory;
            var gifskiDll = Path.Combine(baseDir, "Vendor", "gifski", "gifski.dll");

            using var gifski = Gifski.Create(gifskiDll, settings =>
            {
                settings.Quality = 100;
                settings.Extra = true;
            });

            var outputFolder = CreateTempDirectory();

            var outputFile = Path.Combine(outputFolder, "output.gif");
            gifski.SetFileOutput(outputFile);

            var frames = Directory
                .EnumerateFiles(inputDirectory, "frame_*.png")
                .Select(path =>
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    // e.g. name = "frame_000123"
                    var numPart = name.Substring(name.LastIndexOf('_') + 1);
                    return new
                    {
                        Path = path,
                        Index = int.TryParse(numPart, out var n) ? n : 0
                    };
                })
                .OrderBy(x => x.Index)
                .ToArray();

            if (frames.Length == 0)
            {
                throw new InvalidOperationException("No frames found in " + inputDirectory);
            }

            for (var i = 0; i < frames.Length; i++)
            {
                var timestamp = i / frameRate;
                gifski.AddFramePngFile(
                    frameNumber: (uint)i,
                    presentationTimestamp: timestamp,
                    filePath: frames[i].Path);
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
}
