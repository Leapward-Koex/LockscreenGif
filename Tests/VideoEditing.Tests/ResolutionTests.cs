using System.Buffers.Binary;
using LockscreenGif.Services;

internal static class ResolutionTests
{
    public static async Task RunAsync()
    {
        var root = TempDirectoryService.GetAppTempRoot();
        var landscape = await CreateVideoAsync(Path.Combine(root, "resolution-landscape.mkv"), 2560, 1440);
        foreach (var (width, height) in new[] { (2560, 1440), (1920, 1080), (1280, 720), (854, 480) })
        {
            await CheckExportAsync(landscape, width, height);
        }

        var portrait = await CreateVideoAsync(Path.Combine(root, "resolution-portrait.mkv"), 1080, 1920);
        await CheckExportAsync(portrait, 1080, 1920);
    }

    private static async Task<string> CreateVideoAsync(string path, int width, int height)
    {
        await VideoFrameService.RunAsync(
            ["-f", "lavfi", "-i", $"color=c=purple:size={width}x{height}:rate=10", "-frames:v", "2", "-c:v", "ffv1", path],
            null,
            CancellationToken.None
        );
        return path;
    }

    private static async Task CheckExportAsync(string video, int width, int height)
    {
        var index = await VideoFrameService.IndexAsync(video, 10, null, CancellationToken.None);
        var frames = await VideoFrameService.ExportAsync(video, index, 0, index.Count, width, 0, _ => { });
        var png = File.ReadAllBytes(Directory.GetFiles(frames.Directory, "*.png").Order().First());
        CheckDimensions(
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)),
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)),
            width,
            height,
            "exported PNG"
        );

        var gif = await GifSkiService.CreateGif(frames.Directory, _ => { }, frames.Timestamps, index.Duration);
        var header = File.ReadAllBytes(gif);
        CheckDimensions(
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2)),
            width,
            height,
            "encoded GIF"
        );
    }

    private static void CheckDimensions(int actualWidth, int actualHeight, int width, int height, string stage)
    {
        if (actualWidth != width || actualHeight != height)
        {
            throw new InvalidOperationException($"FAILED: {stage} was {actualWidth}×{actualHeight}; expected {width}×{height}.");
        }

        Console.WriteLine($"PASS {stage} retains selected {width}×{height} resolution");
    }
}
