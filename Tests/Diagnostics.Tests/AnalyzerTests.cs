using LockscreenGif.Models;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Services.Diagnostics;

namespace Diagnostics.Tests;

internal static class AnalyzerTests
{
    internal static void Run()
    {
        var session = Applied();
        session.Snapshots.Add(new() { Reason = "AfterUnlock", Files = [File("GIF-HASH")] });
        var findings = DiagnosticAnalyzer.Analyze(session);
        Program.Check(
            findings.Any(f => f.Title == "GIF retained after unlocking")
                && findings.All(f => !f.Title.Contains("playback", StringComparison.OrdinalIgnoreCase)),
            "Matching cache evidence does not declare successful playback"
        );
        session.Snapshots[0].Files[0].HashReadAt = session.Snapshots[0].Timestamp.AddSeconds(-10);
        Program.Check(
            DiagnosticAnalyzer.Analyze(session).All(f => f.Title != "GIF retained after unlocking"),
            "Stale matching hashes do not establish post-unlock retention"
        );
        session.Snapshots[0].Files[0].HashReadAt = DateTimeOffset.UtcNow;
        session.Snapshots[0].Files[0].HashSource = "Cached; unchanged metadata";
        Program.Check(
            DiagnosticAnalyzer.Analyze(session).All(f => f.Title != "GIF retained after unlocking"),
            "Cached matching hashes do not establish post-unlock retention"
        );
        session.Snapshots[0].Files[0].HashSource = "Full read";
        session.Snapshots[0].Files[0].Sha256 = "CHANGED";
        findings = DiagnosticAnalyzer.Analyze(session);
        Program.Check(findings.Any(f => f.Title == "Cache contents changed after applying"), "A verified file's later change is detected");
        session.Snapshots[0].Files.Clear();
        session.Snapshots[0].Complete = false;
        findings = DiagnosticAnalyzer.Analyze(session);
        Program.Check(
            findings.All(f => f.Title != "Verified files disappeared") && findings.Any(f => f.Title == "Monitoring has gaps"),
            "Failed inventory is not reported as deletion"
        );
        session.Snapshots[0].Complete = true;
        Program.Check(
            DiagnosticAnalyzer.Analyze(session).Any(f => f.Title == "Verified files disappeared"),
            "Complete inventory establishes a missing applied file"
        );
        session = Applied();
        session.LockObserved = false;
        Program.Check(
            DiagnosticAnalyzer.Analyze(session).Any(f => f.Title == "Lock/unlock cycle incomplete"),
            "Missing lock notification makes cycle incomplete"
        );
        session.ApplyResult!.Cancelled = true;
        session.ApplyResult.Success = false;
        Program.Check(
            DiagnosticAnalyzer.Analyze(session).Any(f => f.Title == "Applying was cancelled"),
            "Cancelled apply is distinguished from playback failure"
        );
        session.ComparisonSessionId = "previous";
        Program.Check(
            DiagnosticAnalyzer.Analyze(session).Any(f => f.Title == "Comparison baseline may differ"),
            "Comparison warns that Windows baseline was not reset"
        );
        session = Applied();
        session.Snapshots.Add(new() { Reason = "AfterUnlock", Files = [File("CHANGED", stable: false)] });
        Program.Check(
            DiagnosticAnalyzer.Analyze(session).All(f => f.Title != "Cache contents changed after applying"),
            "Unstable hash is not used as replacement evidence"
        );
    }

    private static DiagnosticSession Applied() =>
        new()
        {
            LockObserved = true,
            UnlockObserved = true,
            Gif = new() { FrameCount = 4, Sha256 = "GIF-HASH" },
            ApplyResult = new LockscreenApplyResult
            {
                Success = true,
                Files =
                [
                    new()
                    {
                        Path = "LockScreen.jpg",
                        Copied = true,
                        Verified = true,
                        VerifiedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                        Sha256 = "GIF-HASH",
                    },
                ],
            },
        };

    private static CacheFileEvidence File(string hash, bool stable = true) =>
        new()
        {
            Path = "LockScreen.jpg",
            Sha256 = hash,
            Stable = stable,
            Format = "GIF",
            HashReadAt = DateTimeOffset.UtcNow,
            HashSource = "Full read",
        };
}
