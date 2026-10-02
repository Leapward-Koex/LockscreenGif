using System.IO.Compression;
using LockscreenGif.Services.Diagnostics;

namespace Session.Tests;

internal static class LifecycleTests
{
    public static async Task RunAsync(string root)
    {
        await ReadOnlyFeatureAndActionsAsync(root);
        await using var context = await TestContext.CreateAsync(root, "complete-and-compare");
        var service = context.Service;
        await service.RefreshReadinessAsync();
        Program.Check(
            service.Readiness.Any(c => c.Name == "Session monitoring" && c.Status == "Ready"),
            "readiness uses registered session monitor"
        );
        await service.StartAsync(false, false);
        Program.Check(service.IsRunning && service.Current?.Phase == "Waiting for lock", "valid source reaches Waiting for lock");
        Program.Check(service.Current!.Gif is { IsAnimated: true, Error: null }, "selected source is inspected before applying");
        Program.Check(
            service.Current!.Snapshots.Any(s => s.Reason == "Baseline") && service.Current.Snapshots.Any(s => s.Reason == "AfterApply"),
            "baseline and apply snapshots captured"
        );
        Program.Check(service.TryLock(out _) && !service.Current!.LockObserved, "lock request alone does not claim lock was observed");

        var lockedAt = DateTimeOffset.UtcNow;
        context.Windows.Emit("SessionLock");
        Program.Check(service.Current!.Phase == "Locked" && service.Current.LockObserved, "native lock observation changes phase");
        await Program.WaitUntilAsync(
            () =>
                service.Current!.Snapshots.Any(s =>
                    s.Timestamp >= lockedAt
                    && s.Files.Count > 0
                    && s.Files.All(f => f.HashReadAt >= lockedAt && f.HashSource.StartsWith("Full read"))
                ),
            "Lock boundary was not freshly hashed."
        );
        Program.Check(true, "lock boundary freshly hashes all files");
        var unlockedAt = DateTimeOffset.UtcNow;
        context.Windows.Emit("SessionUnlock");
        Program.Check(service.Current!.Phase == "Collecting final evidence", "unlock starts final evidence collection");
        await Program.WaitUntilAsync(() => !service.IsRunning, "Unlock collection did not finish.");
        var first = service.Current!;
        Program.Check(first.Phase == "Results" && first.EndedAt is not null && first.UnlockObserved, "unlock completes bounded session");
        Program.Check(
            new[] { "Baseline", "AfterApply", "AfterUnlock" }.All(reason => first.EnvironmentObservations.Any(e => e.Reason == reason)),
            "environment evidence is collected at each key boundary"
        );
        var final = first.Snapshots.Single(s => s.Reason == "AfterUnlock");
        Program.Check(
            final.Files.Count > 0 && final.Files.All(f => f.HashReadAt >= unlockedAt && f.Sha256 == first.Gif!.Sha256),
            "final snapshot freshly verifies selected GIF"
        );
        Program.Check(service.Current!.Id == first.Id, "completed session remains the current result");
        var detached = service.Current!;
        detached.Events.Clear();
        Program.Check(service.Current!.Events.Count > 0, "Current returns an independent snapshot");

        service.RecordObservation("The selected GIF, but still", "Initial lock screen");
        Program.Check(
            service.Current!.Observation == "The selected GIF, but still"
                && service.Current.Findings.Any(f => f.Confidence == "User reported"),
            "observation stays in memory"
        );
        Program.Check(!Directory.Exists(context.StoreDirectory), "test does not create a diagnostic store");
        Program.Check(
            service.Current.SourcePath == context.Lockscreen.CurrentImage!.Path,
            "selected source is used without retaining a copy"
        );
        await service.StartAsync(false, true);
        Program.Check(
            service.Current!.Id != first.Id && service.Current.Observation is null,
            "new test replaces the previous result and observation"
        );
        await context.CompleteLockCycleAsync();
        var export = Path.Combine(context.DirectoryPath, "current.zip");
        await service.ExportAsync(export);
        using var archive = ZipFile.OpenRead(export);
        Program.Check(
            archive.Entries.Count == 4
                && archive.GetEntry("session.json") is not null
                && archive.GetEntry("logs/manifest.json") is not null
                && !archive.Entries.Any(entry => entry.FullName.StartsWith("comparison/")),
            "explicit export contains the current test and application log manifest"
        );
    }

    private static async Task ReadOnlyFeatureAndActionsAsync(string root)
    {
        await using var context = await TestContext.CreateAsync(root, "feature-read-only");
        context.Lockscreen.WindowsImageFeatureAtApply = new()
        {
            QueryStatus = 0,
            RuntimeState = 2,
            RuntimePriority = 0,
            OverrideExists = true,
            OverrideState = 1,
            OverrideOptions = 0,
        };
        DiagnosticsActionLog.Record("OpenLockscreenSettings", "Succeeded", "Before test");
        var blocked = new DiagnosticsSessionService(context.Lockscreen, context.Windows, context.Factory, featureOperationBusy: () => true);
        try
        {
            await blocked.StartAsync(false, false);
            throw new InvalidOperationException("Feature action should block starting a concurrent test.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "Wait for the current operation to finish.")
        {
            Program.Check(
                !blocked.IsRunning && blocked.Current is null,
                "concurrent feature action prevents diagnostic startup without creating a test"
            );
        }
        await context.Service.StartAsync(false, false);
        var session = context.Service.Current!;
        Program.Check(
            context.Service.IsRunning && session.Phase == "Waiting for lock" && session.ApplyResult!.Success,
            "feature state is recorded without deferring the diagnostic lock cycle"
        );
        Program.Check(
            context.Service.TryLock(out _) && session.ApplyResult!.WindowsImageFeatureAtApply!.RuntimeState == 2,
            "enabled runtime and differing stored override do not block locking"
        );
        Program.Check(
            session.PrerequisiteActions.Any(a => a.Detail == "Before test"),
            "actions before test creation are included in the current snapshot"
        );
        session.PrerequisiteActions.Clear();
        Program.Check(context.Service.CurrentForDisplay!.PrerequisiteActions.Count > 0, "display action snapshots are independent copies");
        await context.CompleteLockCycleAsync();
        DiagnosticsActionLog.Record("EnableWindowsImageFeature", "Succeeded", "After finished test");
        var destination = Path.Combine(context.DirectoryPath, "feature-actions.zip");
        await context.Service.ExportAsync(destination);
        using var zip = ZipFile.OpenRead(destination);
        using var reader = new StreamReader(zip.GetEntry("session.json")!.Open());
        var json = await reader.ReadToEndAsync();
        Program.Check(
            json.Contains("Before test") && json.Contains("After finished test") && json.Contains("WindowsImageFeatureAtApply"),
            "export includes actions before and after the test while preserving the apply snapshot"
        );
    }
}
