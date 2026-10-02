using System.Text.Json;
using LockscreenGif.Models;
using LockscreenGif.Services.Lockscreen;

internal static class FailureReasonTests
{
    public static async Task RunAsync()
    {
        await DiscoveryFailures();
        await SourceFailures();
        await FileFailuresAndCancellation();
    }

    private static async Task DiscoveryFailures()
    {
        using var fixture = new CacheFixture(createFolder: false);
        var empty = await fixture.Apply();
        CheckFailure(empty, LockscreenApplyFailureReason.NoDestinations, "choose Picture");
        Check(empty.Files.Count == 0, "An empty cache has no registered file failures.");

        var missingRoot = Path.Combine(fixture.Root, "missing-cache");
        await using var missingPermissions = new CachePermissions(missingRoot, "S-1-5-21-0-0-0-1000", allowElevation: false);
        var missing = await Apply(fixture.Source, missingRoot, missingPermissions);
        CheckFailure(missing, LockscreenApplyFailureReason.CacheMissing, "choose Picture");
        Check(missing.FailureException is DirectoryNotFoundException, "Missing cache retains the original local failure.");

        const string privateDetail = "synthetic-private-path-and-error";
        var failureEvents = new List<LockscreenApplyEvent>();
        await using var deniedPermissions = new CachePermissions(
            fixture.Root,
            "S-1-5-21-0-0-0-1000",
            allowElevation: false,
            readAttributes: path => path == fixture.Root ? throw new UnauthorizedAccessException(privateDetail) : File.GetAttributes(path)
        );
        var denied = await Apply(fixture.Source, fixture.Root, deniedPermissions, failureEvents.Add);
        CheckFailure(denied, LockscreenApplyFailureReason.CacheInaccessible, "permission request");
        Check(denied.Error!.Contains("administrator"), "Persistent denied access provides an escalation action.");
        Check(!denied.Error.Contains("choose Picture"), "Inaccessible cache is not presented as an empty cache.");
        Check(!denied.Error.Contains(privateDetail), "The UI recovery message does not expose exception text.");
        Check(
            denied.Files.Count == 0 && denied.FailureException is UnauthorizedAccessException,
            "Denied discovery remains a zero-target failure."
        );
        Check(
            failureEvents.Any(item => item.Stage == "Failed" && item.Message.Contains("UnauthorizedAccessException")),
            "Local diagnostic evidence retains the failure type."
        );

        await using var brokenPermissions = new CachePermissions(
            fixture.Root,
            "S-1-5-21-0-0-0-1000",
            allowElevation: false,
            readAttributes: _ => throw new IOException(privateDetail)
        );
        var broken = await Apply(fixture.Source, fixture.Root, brokenPermissions);
        CheckFailure(broken, LockscreenApplyFailureReason.CacheDiscoveryFailed, "diagnostic report");
        Check(broken.FailureException is IOException, "Unexpected discovery errors remain distinct from missing and inaccessible caches.");

        var restored = JsonSerializer.Deserialize<LockscreenApplyResult>(JsonSerializer.Serialize(denied))!;
        Check(
            restored.FailureReason == denied.FailureReason && restored.Error == denied.Error,
            "Cloned diagnostics retain actionable failure reasons."
        );
        var legacy = JsonSerializer.Deserialize<LockscreenApplyResult>("{\"Success\":false,\"Files\":[]}")!;
        Check(legacy.FailureReason is null, "Older reports do not fabricate a failure reason.");
    }

    private static async Task SourceFailures()
    {
        using var fixture = new CacheFixture();
        var missingSource = await Apply(Path.Combine(fixture.Root, "missing.gif"), fixture.Root, fixture.Permissions);
        CheckFailure(missingSource, LockscreenApplyFailureReason.SourceReadFailed, "select it again");
        Check(missingSource.FailureException is FileNotFoundException, "Missing source is not classified as missing cache.");

        await File.WriteAllBytesAsync(fixture.Source, [71]);
        var invalid = await fixture.Apply();
        CheckFailure(invalid, LockscreenApplyFailureReason.InvalidSource, "Choose another GIF");
        Check(invalid.Files.Count == 0 && Directory.GetFiles(fixture.Folder).Length == 0, "A truncated source causes no cache writes.");
    }

    private static async Task FileFailuresAndCancellation()
    {
        using var fixture = new CacheFixture();
        var success = await fixture.Apply();
        Check(success.Success && success.FailureReason is null && success.Error is null, "Success has no stale failure reason.");

        await using (var locked = new FileStream(fixture.MainImage, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var partial = await fixture.Apply();
            CheckFailure(partial, LockscreenApplyFailureReason.CopyFailed, "Retry");
            Check(partial.Files.Any(file => file.Verified), "A failed target does not prevent other destinations from applying.");

            using var cancellation = new CancellationTokenSource();
            var cancelled = await fixture.Apply(
                item =>
                {
                    if (item.Stage == "FileFailed")
                    {
                        cancellation.Cancel();
                    }
                },
                cancellation.Token
            );
            Check(cancelled.Cancelled && cancelled.FailureReason is null, "Cancellation clears a reason from an earlier failed target.");
        }

        var mismatch = await fixture.Apply(item =>
        {
            if (item.Stage == "Verifying" && item.Path == fixture.MainImage)
            {
                File.WriteAllText(fixture.MainImage, "changed after commit");
            }
        });
        CheckFailure(mismatch, LockscreenApplyFailureReason.VerificationFailed, "could not be verified");
        Check(mismatch.Files.Any(file => file.Copied && !file.Verified), "Verification failure records the committed copy independently.");
    }

    private static Task<LockscreenApplyResult> Apply(
        string source,
        string root,
        CachePermissions permissions,
        Action<LockscreenApplyEvent>? progress = null
    ) =>
        new LockscreenApplyPipeline(new CacheLayout(root, permissions), new VerifiedCacheWriter(permissions)).ApplyAsync(
            source,
            false,
            new ApplyProgress(progress),
            CancellationToken.None
        );

    private static void CheckFailure(LockscreenApplyResult result, LockscreenApplyFailureReason reason, string recoveryAction) =>
        Check(
            !result.Success && !result.Cancelled && result.FailureReason == reason && result.Error!.Contains(recoveryAction),
            $"Expected {reason} with a corresponding recovery action."
        );

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
