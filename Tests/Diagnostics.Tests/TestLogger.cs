namespace LockscreenGif.Services.Diagnostics;

// Storage tests should not write into the user's real application log directory.
internal static class Logger
{
    public static void Warn(string message) { }

    public static void Info(string message) { }
}
