using LockscreenGif.Services;

internal static class VideoEditingPreferencesTests
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LockscreenGif-video-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            DefaultsAndRoundTrips(Path.Combine(directory, "preferences", "video-editing.json"));
            MalformedPreferencesUseDefault(Path.Combine(directory, "malformed.json"));
            FailedSaveCanRetry(Path.Combine(directory, "blocked.json"));
            if (OperatingSystem.IsWindows())
            {
                FailedReplacementPreservesSavedPreference(Path.Combine(directory, "locked.json"));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void DefaultsAndRoundTrips(string path)
    {
        var preferences = new VideoEditingPreferences(path);
        Require(
            preferences.UseHardwareDecoding && !preferences.HasSaveError,
            "Missing preferences should default to hardware decoding enabled."
        );
        Require(!File.Exists(path), "Reading a default preference should not create a file.");

        var changed = new HashSet<string?>();
        preferences.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        preferences.UseHardwareDecoding = false;
        Require(!preferences.HasSaveError, "Saving the off preference should succeed.");
        Require(!new VideoEditingPreferences(path).UseHardwareDecoding, "A saved off preference should survive a restart.");
        Require(changed.Contains(nameof(VideoEditingPreferences.UseHardwareDecoding)), "Preference changes should notify Settings.");
        Require(changed.Contains(nameof(VideoEditingPreferences.HasSaveError)), "Save state should notify Settings.");

        preferences.UseHardwareDecoding = true;
        Require(new VideoEditingPreferences(path).UseHardwareDecoding, "An enabled preference should survive a restart.");
        Require(
            Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0,
            "Successful writes should leave no temporary files."
        );
    }

    private static void MalformedPreferencesUseDefault(string path)
    {
        File.WriteAllText(path, "not json");
        Require(new VideoEditingPreferences(path).UseHardwareDecoding, "Malformed preferences should use the automatic decoding default.");
        File.WriteAllText(path, "{}");
        Require(new VideoEditingPreferences(path).UseHardwareDecoding, "An omitted preference should use the enabled default.");
    }

    private static void FailedSaveCanRetry(string path)
    {
        Directory.CreateDirectory(path);
        var preferences = new VideoEditingPreferences(path) { UseHardwareDecoding = false };
        Require(
            !preferences.UseHardwareDecoding && preferences.HasSaveError,
            "A failed write should retain the active session preference and warn."
        );
        Require(Directory.Exists(path), "A failed atomic write must not remove the existing target.");
        Require(
            Directory.GetFiles(Path.GetDirectoryName(path)!, "blocked.json.*.tmp").Length == 0,
            "Failed writes should clean up temporary files."
        );

        Directory.Delete(path);
        preferences.UseHardwareDecoding = false;
        Require(!preferences.HasSaveError, "Setting the same value after a save error should retry persistence.");
        Require(!new VideoEditingPreferences(path).UseHardwareDecoding, "The retried preference should be saved.");
    }

    private static void FailedReplacementPreservesSavedPreference(string path)
    {
        File.WriteAllText(path, "{\"UseHardwareDecoding\":true}");
        var preferences = new VideoEditingPreferences(path);
        using (var savedFile = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            preferences.UseHardwareDecoding = false;
            Require(
                preferences.HasSaveError && !preferences.UseHardwareDecoding,
                "A locked saved file should keep the new preference active with a warning."
            );
        }
        Require(new VideoEditingPreferences(path).UseHardwareDecoding, "Failed replacement should preserve the prior saved value.");
        preferences.UseHardwareDecoding = false;
        Require(
            !preferences.HasSaveError && !new VideoEditingPreferences(path).UseHardwareDecoding,
            "Persistence should recover after the file is unlocked."
        );
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
