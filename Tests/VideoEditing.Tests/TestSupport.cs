namespace LockscreenGif.Services
{
    internal static class TempDirectoryService
    {
        internal static readonly string Root = Path.Combine(
            Path.GetTempPath(),
            "LockscreenGif-video-tests-" + Guid.NewGuid().ToString("N")
        );

        public static string GetAppTempRoot()
        {
            Directory.CreateDirectory(Root);
            return Root;
        }
    }

    internal static class FfmpegService
    {
        public static string CreateTempDirectory()
        {
            var path = Path.Combine(TempDirectoryService.GetAppTempRoot(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}

internal static class Logger
{
    public static void Info(string message) { }

    public static void Error(string message, Exception exception) => Console.Error.WriteLine(message + exception.Message);
}
