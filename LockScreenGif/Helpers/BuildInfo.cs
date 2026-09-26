using System.Reflection;

namespace LockscreenGif.Helpers;

public static class BuildInfo
{
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static string DisplayVersion => $"Lockscreen Gif {Version}";
}
