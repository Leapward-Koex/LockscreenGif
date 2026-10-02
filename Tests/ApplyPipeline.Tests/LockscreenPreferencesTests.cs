using LockscreenGif.Services;

internal static class LockscreenPreferencesTests
{
    public static Task Persistence() =>
        WithSettings(path =>
        {
            var preferences = new LockscreenPreferences(path);
            Assert(
                !preferences.UseWindowsApi && !File.Exists(path),
                "New installations must default off without creating a preference file."
            );
            preferences.UseWindowsApi = true;
            Assert(!preferences.HasSaveError && new LockscreenPreferences(path).UseWindowsApi, "Enabling must survive an app restart.");
            preferences.UseWindowsApi = false;
            Assert(!preferences.HasSaveError && !new LockscreenPreferences(path).UseWindowsApi, "Disabling must survive an app restart.");
            Assert(Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1, "Successful saves must not leave temporary files.");
        });

    public static Task InvalidSettings() =>
        WithSettings(path =>
        {
            foreach (var content in new[] { "{}", "null", "invalid", "{\"UseWindowsApi\":\"true\"}", "{\"UseWindowsApi\":1}" })
            {
                File.WriteAllText(path, content);
                Assert(
                    !new LockscreenPreferences(path).UseWindowsApi && File.ReadAllText(path) == content,
                    "Invalid saved values must default off without overwriting the file."
                );
            }
        });

    public static Task SaveFailure() =>
        WithSettings(path =>
        {
            var preferences = new LockscreenPreferences(path) { UseWindowsApi = true };
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                preferences.UseWindowsApi = false;
                Assert(
                    preferences.HasSaveError && !preferences.UseWindowsApi,
                    "A failed save must retain the requested session value and report the error."
                );
                Assert(new LockscreenPreferences(path).UseWindowsApi, "Failed replacement must preserve the prior saved preference.");
                Assert(Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1, "A failed save must remove its temporary file.");
            }
            preferences.UseWindowsApi = false;
            Assert(
                !preferences.HasSaveError && !new LockscreenPreferences(path).UseWindowsApi,
                "Retrying the same session value must save and clear the warning."
            );
        });

    public static Task SharedNotifications() =>
        WithSettings(path =>
        {
            var preferences = new LockscreenPreferences(path);
            var first = new List<string?>();
            var second = new List<string?>();
            System.ComponentModel.PropertyChangedEventHandler firstHandler = (_, args) => first.Add(args.PropertyName);
            System.ComponentModel.PropertyChangedEventHandler secondHandler = (_, args) => second.Add(args.PropertyName);
            preferences.PropertyChanged += firstHandler;
            preferences.PropertyChanged += secondHandler;
            preferences.UseWindowsApi = true;
            var expected = new[] { nameof(LockscreenPreferences.UseWindowsApi), nameof(LockscreenPreferences.HasSaveError) };
            Assert(
                first.SequenceEqual(expected) && second.SequenceEqual(expected),
                "Every active consumer must receive preference and save-status updates."
            );
            preferences.PropertyChanged -= firstHandler;
            preferences.UseWindowsApi = false;
            Assert(
                first.Count == 2 && second.Count == 4,
                "Detached consumers must stop receiving updates while the shared preference remains active."
            );
        });

    private static Task WithSettings(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LockscreenGif-windows-api-preferences-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            test(Path.Combine(directory, "lockscreen.json"));
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            if (
                Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(resolved).StartsWith("LockscreenGif-windows-api-preferences-tests-", StringComparison.Ordinal)
            )
            {
                throw new InvalidOperationException("Unexpected Windows API preference cleanup directory.");
            }
            Directory.Delete(resolved, recursive: true);
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
