using Microsoft.Win32;

namespace LockscreenGif.Services.Lockscreen;

internal static class LockscreenSettings
{
    public static LockscreenService.LockScreenMode InferMode()
    {
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lock Screen");
            using var delivery = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager");
            var slideshow = settings?.GetValue("SlideshowEnabled");
            var spotlight = delivery?.GetValue("RotatingLockScreenEnabled");
            var slideshowValueMissing =
                settings is not null
                && slideshow is null
                && !settings.GetValueNames().Contains("SlideshowEnabled", StringComparer.OrdinalIgnoreCase);
            // CreativeId/CreativeJson can remain after leaving Spotlight. Read the selection
            // switches instead. Windows can leave SlideshowEnabled absent after selecting Picture;
            // accept that only when its name is confirmed absent from an existing key and
            // Spotlight is explicitly disabled. A null read alone does not establish absence.
            return InferMode(slideshow, spotlight, slideshowValueMissing);
        }
        catch
        {
            return LockscreenService.LockScreenMode.Unknown;
        }
    }

    internal static LockscreenService.LockScreenMode InferMode(object? slideshow, object? spotlight, bool slideshowValueMissing = false)
    {
        var slide = ReadSwitch(slideshow);
        var rotate = ReadSwitch(spotlight);
        if (slide == true && rotate == true)
        {
            return LockscreenService.LockScreenMode.Unknown;
        }
        if (slide == true)
        {
            return LockscreenService.LockScreenMode.Slideshow;
        }
        if (rotate == true)
        {
            return LockscreenService.LockScreenMode.Spotlight;
        }
        return (slide == false || slideshow is null && slideshowValueMissing) && rotate == false
            ? LockscreenService.LockScreenMode.PictureOrOther
            : LockscreenService.LockScreenMode.Unknown;
    }

    private static bool? ReadSwitch(object? value) =>
        value switch
        {
            int number when number is 0 or 1 => number == 1,
            byte[] { Length: 1 } bytes when bytes[0] is 0 or 1 => bytes[0] == 1,
            _ => null,
        };
}
