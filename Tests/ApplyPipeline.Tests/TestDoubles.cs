namespace LockscreenGif.Services
{
    internal static class DisplayService
    {
        public static IEnumerable<string> GetDisplayResolutions() => ["1920_1080"];
    }
}

internal static class Logger
{
    public static void Info(string message) { }

    public static void Error(string message, Exception? exception = null) { }
}
