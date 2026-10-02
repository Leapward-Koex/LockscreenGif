using LockscreenGif.Privileged;

namespace LockscreenGif.Services.Lockscreen;

internal sealed record PrerequisiteStatus(bool Satisfied, bool CanAct, string Detail);

internal sealed record LockscreenPrerequisiteStatus(PrerequisiteStatus PictureAndCache, PrerequisiteStatus ImageFeature)
{
    public bool Satisfied => PictureAndCache.Satisfied && ImageFeature.Satisfied;
}

/// <summary>Read-only prerequisite checks. An unreadable cache or unknown preference never counts as ready.</summary>
internal static class LockscreenPrerequisites
{
    public static LockscreenPrerequisiteStatus Read(string cacheDirectory)
    {
        var mode = LockscreenSettings.InferMode();
        int? folders = null;
        try
        {
            folders = CountCacheFolders(cacheDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // No access repair or elevation during a prerequisite read.
        }
        return Evaluate(mode, folders, Environment.OSVersion.Version, WindowsImageFeatureSettings.Current.Read());
    }

    internal static int CountCacheFolders(string cacheDirectory)
    {
        try
        {
            return Directory
                .EnumerateDirectories(cacheDirectory, "LockScreen*", SearchOption.TopDirectoryOnly)
                .Count(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0);
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
    }

    internal static bool Is25H2OrLater(Version version) => version >= new Version(10, 0, 26200);

    internal static LockscreenPrerequisiteStatus Evaluate(
        LockscreenService.LockScreenMode mode,
        int? folders,
        Version version,
        WindowsImageFeatureState feature
    )
    {
        var picture = mode == LockscreenService.LockScreenMode.PictureOrOther;
        var modeText = mode switch
        {
            LockscreenService.LockScreenMode.PictureOrOther => "Picture is selected.",
            LockscreenService.LockScreenMode.Slideshow => "Slideshow is selected. Change it to Picture.",
            LockscreenService.LockScreenMode.Spotlight => "Windows Spotlight is selected. Change it to Picture.",
            _ => "Picture mode could not be confirmed. Select Picture in Windows Settings.",
        };
        var cacheText = folders switch
        {
            > 0 => "A lock-screen cache folder is present.",
            0 => "No lock-screen cache folder exists yet. Choose a picture, then lock and unlock Windows once.",
            _ => "The lock-screen cache could not be checked. Choose a picture, then lock and unlock Windows once.",
        };
        return new(new(picture && folders > 0, true, modeText + " " + cacheText), ImageFeature(version, feature));
    }

    internal static PrerequisiteStatus ImageFeature(Version version, WindowsImageFeatureState feature)
    {
        var description = $"Windows feature {feature.FeatureId}: ";
        var custom = feature.FeatureId != WindowsImageFeature.DefaultFeatureId;
        var compatibility = custom
            ? "This custom feature ID has not been verified for animated GIF compatibility."
            : "The animated GIF compatibility setting is configured.";
        if (!Is25H2OrLater(version))
        {
            return new(
                true,
                false,
                description + "Not required on this version of Windows. This prerequisite applies to Windows 11 25H2 and later."
            );
        }
        var evaluation = WindowsImageFeature.Evaluate(feature);
        return evaluation.Outcome switch
        {
            "AlreadyDisabled" => new(true, false, description + "Disabled. " + compatibility),
            "NeedsChange" => new(
                false,
                true,
                description
                    + (feature.RuntimeState == 1 ? "Currently disabled, but its saved setting is enabled. " : "Enabled. ")
                    + (custom ? compatibility : "Disable it to configure the animated GIF compatibility setting.")
            ),
            _ => new(
                false,
                false,
                description
                    + "Its status could not be confirmed or changed. The feature may be absent on this Windows build. If your GIF already animates, you can continue. Run a diagnostic test for details."
            ),
        };
    }
}
