using LockscreenGif.Privileged;
using LockscreenGif.Services;
using LockscreenGif.Services.Lockscreen;

var checks = 0;
void Check(bool value, string description)
{
    if (!value)
    {
        throw new InvalidOperationException(description);
    }
    Console.WriteLine("PASS " + description);
    checks++;
}

var current = new Version(10, 0, 26200);
var disabled = new WindowsImageFeatureState
{
    QueryStatus = 0,
    RuntimeState = 1,
    RuntimePriority = 0,
    OverrideExists = false,
};
var enabled = new WindowsImageFeatureState
{
    QueryStatus = 0,
    RuntimeState = 2,
    RuntimePriority = 0,
    OverrideExists = false,
};
Check(LockscreenSettings.InferMode(0, 0) == LockscreenService.LockScreenMode.PictureOrOther, "Both explicit off switches identify Picture");
var pictureWithMissingSlideshow = LockscreenSettings.InferMode(null, 0, slideshowValueMissing: true);
Check(
    pictureWithMissingSlideshow == LockscreenService.LockScreenMode.PictureOrOther,
    "An absent Slideshow value under an existing key identifies Picture when Spotlight is explicitly off"
);
Check(
    LockscreenSettings.InferMode(null, 0) == LockscreenService.LockScreenMode.Unknown,
    "A null Slideshow read without confirmed absence never identifies Picture"
);
Check(
    LockscreenSettings.InferMode(null, 0, slideshowValueMissing: false) == LockscreenService.LockScreenMode.Unknown,
    "An absent Lock Screen key or a present Slideshow name with unreadable data never identifies Picture"
);
Check(LockscreenSettings.InferMode(1, 0) == LockscreenService.LockScreenMode.Slideshow, "Slideshow does not satisfy Picture");
Check(LockscreenSettings.InferMode(0, 1) == LockscreenService.LockScreenMode.Spotlight, "Spotlight does not satisfy Picture");
Check(
    LockscreenSettings.InferMode(null, 1, slideshowValueMissing: true) == LockscreenService.LockScreenMode.Spotlight,
    "An absent Slideshow value never overrides explicitly enabled Spotlight"
);
foreach (
    var pair in new (object? Slide, object? Spotlight)[]
    {
        (null, null),
        (0, null),
        (1, 1),
        ("0", 0),
        (7, 0),
        (Array.Empty<byte>(), 0),
        (new byte[] { 0, 0 }, 0),
        (new byte[] { 7 }, 0),
        (0, 7),
        (null, "0"),
        (null, new byte[] { 0, 0 }),
    }
)
{
    Check(
        LockscreenSettings.InferMode(pair.Slide, pair.Spotlight) == LockscreenService.LockScreenMode.Unknown,
        "Missing Spotlight, malformed or conflicting switch values never report Picture"
    );
    Check(
        LockscreenSettings.InferMode(pair.Slide, pair.Spotlight, slideshowValueMissing: true) == LockscreenService.LockScreenMode.Unknown,
        "The absence flag cannot override malformed, conflicting or missing Spotlight data"
    );
}
Check(
    LockscreenPrerequisites.Evaluate(LockscreenService.LockScreenMode.PictureOrOther, 1, current, disabled).Satisfied,
    "Picture plus an existing cache and a disabled feature satisfy both entries"
);
Check(
    LockscreenPrerequisites.Evaluate(pictureWithMissingSlideshow, 1, current, disabled).Satisfied,
    "Picture inferred from an absent Slideshow value clears the prerequisite with an existing cache and a disabled feature"
);
foreach (var count in new int?[] { 0, null })
{
    var result = LockscreenPrerequisites.Evaluate(pictureWithMissingSlideshow, count, current, disabled);
    Check(
        !result.PictureAndCache.Satisfied && !result.Satisfied && result.PictureAndCache.CanAct,
        "Picture alone is insufficient with an absent or unreadable cache"
    );
}
foreach (
    var mode in new[]
    {
        LockscreenService.LockScreenMode.Slideshow,
        LockscreenService.LockScreenMode.Spotlight,
        LockscreenService.LockScreenMode.Unknown,
    }
)
{
    Check(
        !LockscreenPrerequisites.Evaluate(mode, 1, current, disabled).PictureAndCache.Satisfied,
        "An existing cache alone never establishes Picture mode"
    );
}
Check(
    LockscreenPrerequisites.ImageFeature(new Version(10, 0, 26100), enabled) is { Satisfied: true, CanAct: false },
    "Windows 24H2 does not require disabling the feature"
);
foreach (var version in new[] { current, new Version(10, 0, 28000), new Version(11, 0, 100) })
{
    Check(
        LockscreenPrerequisites.ImageFeature(version, enabled) is { Satisfied: false, CanAct: true },
        "Windows 25H2 and later offer the disable action for an enabled feature"
    );
}
Check(
    LockscreenPrerequisites.ImageFeature(current, new()) is { Satisfied: false, CanAct: false },
    "Unknown feature state needs attention without authorizing a blind write"
);
var pending = new WindowsImageFeatureState
{
    QueryStatus = 0,
    RuntimeState = 2,
    RuntimePriority = 0,
    OverrideExists = true,
    OverrideState = 1,
    OverrideOptions = 0,
};
Check(
    LockscreenPrerequisites.ImageFeature(current, pending) is { Satisfied: false, CanAct: true },
    "A persisted disable with enabled runtime offers the action without a restart gate"
);

var root = Path.Combine(Path.GetTempPath(), "LockscreenGif-prerequisites-tests-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(root);
    Check(LockscreenPrerequisites.CountCacheFolders(root) == 0, "Empty cache has no valid folders");
    File.WriteAllText(Path.Combine(root, "LockScreen.jpg"), "fixture");
    Directory.CreateDirectory(Path.Combine(root, "Other"));
    Directory.CreateDirectory(Path.Combine(root, "Other", "LockScreen_nested"));
    Check(LockscreenPrerequisites.CountCacheFolders(root) == 0, "Images and nested or unrelated directories are not cache folders");
    Directory.CreateDirectory(Path.Combine(root, "LockScreen_A"));
    Check(LockscreenPrerequisites.CountCacheFolders(root) == 1, "An immediate lock-screen cache folder is detected");
    Check(LockscreenPrerequisites.CountCacheFolders(Path.Combine(root, "missing")) == 0, "A missing cache is not ready");
}
finally
{
    var resolved = Path.GetFullPath(root);
    if (
        Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
        || !Path.GetFileName(resolved).StartsWith("LockscreenGif-prerequisites-tests-", StringComparison.Ordinal)
    )
    {
        throw new InvalidOperationException("Unexpected cleanup directory");
    }
    Directory.Delete(resolved, recursive: true);
}
FeaturePreferenceTests.Run(Check);
SessionMonitorTests.Run(Check);
Console.WriteLine($"{checks} prerequisite, feature preference and session monitor checks passed. No Windows feature settings changed.");

namespace LockscreenGif.Services
{
    public static class LockscreenService
    {
        public enum LockScreenMode
        {
            PictureOrOther,
            Slideshow,
            Spotlight,
            Unknown,
        }
    }
}
