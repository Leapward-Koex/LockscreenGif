using Microsoft.Win32;

namespace LockscreenGif.Services.Lockscreen;

internal static class LockscreenSettings
{
    public static LockscreenService.LockScreenMode InferMode()
    {
        try
        {
            using var creative = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lock Screen\Creative");
            if (
                !string.IsNullOrWhiteSpace(creative?.GetValue("CreativeId") as string)
                || !string.IsNullOrWhiteSpace(creative?.GetValue("CreativeJson") as string)
            )
            {
                return LockscreenService.LockScreenMode.Spotlight;
            }

            using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lock Screen");
            var value = settings?.GetValue("SlideshowEnabled");
            if (value is int number && number == 1 || value is byte[] { Length: > 0 } bytes && bytes[0] == 1)
            {
                return LockscreenService.LockScreenMode.Slideshow;
            }

            // These undocumented values are only a heuristic. Missing settings are unknown.
            return settings is null ? LockscreenService.LockScreenMode.Unknown : LockscreenService.LockScreenMode.PictureOrOther;
        }
        catch
        {
            return LockscreenService.LockScreenMode.Unknown;
        }
    }
}
