using System.IO.Compression;
using LockscreenGif.Services.Diagnostics;

namespace Session.Tests;

internal static class LifecycleTests
{
    public static async Task RunAsync(string root)
    {
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
            archive.Entries.Count == 3 && archive.GetEntry("session.json") is not null,
            "explicit export contains only the current test"
        );
    }
}
