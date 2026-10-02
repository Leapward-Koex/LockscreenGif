using LockscreenGif.Services;

internal static class LockscreenPreferencesTests
{
    public static Task Persistence() =>
        WithSettings(path =>
        {
            var preferences = new LockscreenPreferences(path);
            Assert(!preferences.UseWindowsApi, "New installations must default off.");
            var notifications = new List<string?>();
            preferences.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
            preferences.UseWindowsApi = true;
            Assert(!preferences.HasSaveError && new LockscreenPreferences(path).UseWindowsApi, "Enabling must survive a restart.");
            Assert(
                notifications.Contains(nameof(LockscreenPreferences.UseWindowsApi)),
                "Both UI bindings must receive preference changes."
            );
            preferences.UseWindowsApi = false;
            Assert(!preferences.HasSaveError && !new LockscreenPreferences(path).UseWindowsApi, "Disabling must survive a restart.");
        });

    public static Task InvalidSettings() =>
        WithSettings(path =>
        {
            foreach (var contents in new[] { "{}", "null", "invalid", "{\"UseWindowsApi\":\"true\"}" })
            {
                File.WriteAllText(path, contents);
                Assert(!new LockscreenPreferences(path).UseWindowsApi, "Missing or invalid values must default off.");
            }
        });

    public static Task SaveFailure() =>
        WithSettings(path =>
        {
            var preferences = new LockscreenPreferences(path) { UseWindowsApi = true };
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                preferences.UseWindowsApi = false;
                Assert(preferences.HasSaveError && !preferences.UseWindowsApi, "A failed save must report the unsaved session preference.");
                Assert(new LockscreenPreferences(path).UseWindowsApi, "Failed replacement must preserve the previous saved preference.");
            }
            preferences.UseWindowsApi = false;
            Assert(!preferences.HasSaveError && !new LockscreenPreferences(path).UseWindowsApi, "Retry must save and clear the warning.");
        });

    private static Task WithSettings(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LockscreenGif-preferences-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            test(Path.Combine(directory, "lockscreen.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
