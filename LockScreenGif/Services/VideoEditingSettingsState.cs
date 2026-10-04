namespace LockscreenGif.Services;

/// <summary>The visible setting state never overwrites the user's saved decoding preference.</summary>
public sealed record VideoEditingSettingsState(bool IsEnabled, bool IsOn, string Description)
{
    public static VideoEditingSettingsState Create(HardwareDecodingAvailability availability, bool useHardwareDecoding) =>
        availability switch
        {
            HardwareDecodingAvailability.Checking => new(false, false, "Checking hardware decoding availability…"),
            HardwareDecodingAvailability.Available => new(true, useHardwareDecoding, "Hardware decoding is available."),
            HardwareDecodingAvailability.Unavailable => new(
                false,
                false,
                "Hardware decoding isn’t available on this machine. Videos will use CPU decoding."
            ),
            _ => new(
                false,
                false,
                "Hardware decoding availability could not be verified. Videos will use CPU decoding. Open Settings again to retry."
            ),
        };
}
