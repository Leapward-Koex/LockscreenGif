using LockscreenGif.Services;

internal static class VideoEditingSettingsStateTests
{
    public static void Run()
    {
        var checking = VideoEditingSettingsState.Create(HardwareDecodingAvailability.Checking, true);
        Require(!checking.IsEnabled && !checking.IsOn, "Checking should disable the setting without advertising active hardware decoding.");
        Require(checking.Description == "Checking hardware decoding availability…", "Checking should explain why the setting is disabled.");

        var available = VideoEditingSettingsState.Create(HardwareDecodingAvailability.Available, true);
        Require(available.IsEnabled && available.IsOn, "Supported machines should expose the enabled preference.");
        var savedOff = VideoEditingSettingsState.Create(HardwareDecodingAvailability.Available, false);
        Require(savedOff.IsEnabled && !savedOff.IsOn, "Supported machines must respect a saved off preference.");

        var unavailable = VideoEditingSettingsState.Create(HardwareDecodingAvailability.Unavailable, true);
        Require(!unavailable.IsEnabled && !unavailable.IsOn, "Unsupported machines should disable the toggle and display it off.");
        Require(
            unavailable.Description == "Hardware decoding isn’t available on this machine. Videos will use CPU decoding.",
            "Unsupported machines should explain the CPU decoding behavior."
        );

        var failed = VideoEditingSettingsState.Create(HardwareDecodingAvailability.CheckFailed, true);
        Require(!failed.IsEnabled && !failed.IsOn, "An inconclusive capability check should disable the toggle.");
        Require(
            failed.Description.Contains("could not be verified", StringComparison.Ordinal),
            "A failed check should be distinct from unsupported hardware."
        );

        PreferenceIsRestoredAfterAvailabilityChanges();
    }

    private static void PreferenceIsRestoredAfterAvailabilityChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LockscreenGif-video-setting-state-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "video-editing.json");
        Directory.CreateDirectory(directory);
        try
        {
            var preferences = new VideoEditingPreferences(path) { UseHardwareDecoding = false };
            preferences.UseHardwareDecoding = true;
            var savedContents = File.ReadAllText(path);
            foreach (
                var availability in new[]
                {
                    HardwareDecodingAvailability.Checking,
                    HardwareDecodingAvailability.Unavailable,
                    HardwareDecodingAvailability.CheckFailed,
                }
            )
            {
                _ = VideoEditingSettingsState.Create(availability, preferences.UseHardwareDecoding);
                Require(preferences.UseHardwareDecoding, "Temporary unavailability must preserve the session preference.");
                Require(File.ReadAllText(path) == savedContents, "Temporary unavailability must not rewrite the saved preference.");
            }
            var restored = VideoEditingSettingsState.Create(HardwareDecodingAvailability.Available, preferences.UseHardwareDecoding);
            Require(restored.IsEnabled && restored.IsOn, "A saved enabled preference should return when hardware becomes available.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
