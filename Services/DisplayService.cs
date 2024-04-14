using WindowsDisplayAPI;

namespace LockscreenGif.Services;
public class DisplayService
{
    public static IEnumerable<string> GetDisplayResolutions()
    {
        return Display.GetDisplays().Select(GetResolution).Distinct();
    }

    private static string GetResolution(Display display)
    {
        return display.CurrentSetting.Resolution.Width.ToString("0000") + "_" + display.CurrentSetting.Resolution.Height.ToString("0000");
    }
}
