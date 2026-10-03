using LockscreenGif.Privileged;
using LockscreenGif.Services.Lockscreen;

internal static class FeaturePreferenceTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var text in new string?[] { null, "", " ", "0", "-1", "+1", "0x2536787", "1.0", "1e2", "1,000", "4294967296", "name" })
        {
            check(
                !WindowsImageFeatureSettings.TryParseFeatureId(text, out _),
                "Feature ID validation rejects non-positive or non-decimal values"
            );
        }
        foreach (var (text, expected) in new[] { ("1", 1u), (" 38943831 ", 38943831u), ("00042", 42u), ("4294967295", uint.MaxValue) })
        {
            check(
                WindowsImageFeatureSettings.TryParseFeatureId(text, out var parsed) && parsed == expected,
                "Feature ID validation accepts positive decimal uint values"
            );
        }

        var root = Path.Combine(Path.GetTempPath(), "LockscreenGif-feature-preference-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "preferences", "windows-image-feature.json");
            var settings = new WindowsImageFeatureSettings(path);
            check(
                settings.FeatureId == WindowsImageFeature.DefaultFeatureId && !Directory.Exists(Path.GetDirectoryName(path)),
                "Loading an absent preference uses the default without creating files"
            );
            settings.Save(42);
            check(
                settings.FeatureId == 42 && new WindowsImageFeatureSettings(path).FeatureId == 42,
                "Saving the feature ID persists the same value for the next app session"
            );
            check(Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1, "A successful preference save leaves no temporary files");
            settings.Reset();
            check(
                settings.FeatureId == WindowsImageFeature.DefaultFeatureId
                    && new WindowsImageFeatureSettings(path).FeatureId == WindowsImageFeature.DefaultFeatureId,
                "Reset persists the default feature ID"
            );
            foreach (
                var invalid in new[]
                {
                    "broken-json",
                    "{}",
                    "null",
                    "{\"FeatureId\":0}",
                    "{\"FeatureId\":-1}",
                    "{\"FeatureId\":4294967296}",
                    "{\"FeatureId\":\"42\"}",
                }
            )
            {
                File.WriteAllText(path, invalid);
                var fallback = new WindowsImageFeatureSettings(path);
                check(
                    fallback.FeatureId == WindowsImageFeature.DefaultFeatureId && File.ReadAllText(path) == invalid,
                    "Invalid persisted feature IDs fall back to default without rewriting the file"
                );
            }

            settings.Save(73);
            var previous = File.ReadAllText(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Throws(() => settings.Save(74));
                check(
                    settings.FeatureId == 73 && File.ReadAllText(path) == previous,
                    "A failed atomic replacement preserves both the active ID and the previous preference"
                );
                Throws(settings.Reset);
                check(
                    settings.FeatureId == 73 && File.ReadAllText(path) == previous,
                    "A failed reset does not alter the active or saved ID"
                );
            }
            check(Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1, "Failed preference saves remove their own temporary files");
            Throws(() => settings.Save(0));
            check(
                settings.FeatureId == 73 && File.ReadAllText(path) == previous,
                "Programmatic zero IDs are rejected without persistence changes"
            );
            var blockedParent = Path.Combine(root, "parent-is-a-file");
            File.WriteAllText(blockedParent, "synthetic blocker");
            var blocked = new WindowsImageFeatureSettings(Path.Combine(blockedParent, "preference.json"));
            Throws(() => blocked.Save(74));
            check(
                blocked.FeatureId == WindowsImageFeature.DefaultFeatureId && File.ReadAllText(blockedParent) == "synthetic blocker",
                "An inaccessible preference destination leaves the selected ID unchanged"
            );

            var custom = new WindowsImageFeatureState
            {
                FeatureId = 73,
                QueryStatus = 0,
                RuntimeState = 1,
                RuntimePriority = 0,
                OverrideExists = false,
            };
            var status = LockscreenPrerequisites.ImageFeature(new Version(10, 0, 26200), custom);
            check(
                status.Detail.Contains("73") && status.Detail.Contains("not been verified"),
                "Custom feature prerequisites display the selected ID without promising GIF compatibility"
            );
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (
                Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(resolved).StartsWith("LockscreenGif-feature-preference-tests-", StringComparison.Ordinal)
            )
            {
                throw new InvalidOperationException("Unexpected preference test cleanup directory.");
            }
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static void Throws(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return;
        }
        throw new InvalidOperationException("The invalid preference write unexpectedly succeeded.");
    }
}

internal static class Logger
{
    public static void Info(string message) { }

    public static void Warn(string message) { }

    public static void Error(string message, Exception? error = null) { }
}
