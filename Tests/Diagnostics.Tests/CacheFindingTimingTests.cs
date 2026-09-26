using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Services.Diagnostics;
using LockscreenGif.ViewModels;
using Microsoft.UI.Xaml;

namespace Diagnostics.Tests;

internal static class CacheFindingTimingTests
{
    private static readonly DateTimeOffset Boundary = DateTimeOffset.Parse("2026-01-01T00:00:10Z");
    private const string Target = @"C:\cache\LockScreen_A\LockScreen.jpg";

    internal static void Run()
    {
        var session = Session();
        session.Snapshots = [Snapshot(-5, "OLD", "Baseline"), Snapshot(1, "GIF", "AfterApply")];
        Program.Check(
            !DiagnosticCacheFindings.Analyze(session).Any()
                && TraceHashCorrelation.Describe(session, Target).Contains("matched the applied GIF"),
            "Expected baseline-to-applied hash change is not a replacement warning"
        );

        session.Snapshots[1].Files[0].Sha256 = "DIFFERENT";
        Program.Check(Changed(session), "A mismatch in AfterApply is compared with the verified applied hash");
        session.Snapshots.Add(Snapshot(3, "GIF", "AfterUnlock"));
        Program.Check(
            Changed(session)
                && TraceHashCorrelation.Describe(session, Target).Contains("differed")
                && DiagnosticFileFindings.AfterUnlock(session)!.Severity == "Success",
            "Temporary content changes remain visible even when final fresh hashes match again"
        );

        session.Snapshots = [Snapshot(-1, "DIFFERENT", "AfterApply")];
        session.Snapshots[0].Files[0].HashReadAt = Boundary.AddSeconds(1);
        Program.Check(!Changed(session), "A snapshot begun before verification cannot establish later replacement");
        session.Snapshots = [Snapshot(1, "DIFFERENT", "AfterApply")];
        session.Snapshots[0].Files[0].HashSource = "Cached; unchanged metadata";
        Program.Check(!Changed(session), "Reused hash values do not establish fresh replacement evidence");
        session.Snapshots[0].Files[0].HashSource = "Full read";
        session.Snapshots[0].Files[0].HashReadAt = Boundary;
        Program.Check(!Changed(session), "Hash reads preceding snapshot capture are not fresh evidence");
        session.Snapshots[0].Files[0].HashReadAt = Boundary.AddSeconds(2);
        session.Snapshots[0].Files[0].Error = "Unreadable";
        Program.Check(!Changed(session), "A failed hash inspection cannot establish changed contents");

        session.Snapshots = [Snapshot(-1, null, "AfterApply")];
        Program.Check(!Missing(session), "A complete inventory before verification does not prove deletion");
        session.Snapshots = [Snapshot(1, null, "AfterApply")];
        session.Snapshots[0].Complete = false;
        Program.Check(!Missing(session), "An incomplete inventory after verification does not prove deletion");
        session.Snapshots[0].Complete = true;
        Program.Check(Missing(session), "A complete inventory after verification establishes a missing target");

        session.Snapshots = [Snapshot(1, "DIFFERENT", "AfterUnlock")];
        session.ApplyResult!.Files[0].VerifiedAt = null;
        Program.Check(
            !Changed(session) && !Missing(session) && DiagnosticFileFindings.AfterUnlock(session)!.Severity == "Warning",
            "Missing verification time is unknown, not proof of a later change or retained copy"
        );
        session.ApplyResult.Files[0].VerifiedAt = Boundary;
        session.ApplyResult.Files[0].Error = "Commit failed";
        Program.Check(!Changed(session), "A failed copy cannot anchor a post-verification hash change");
        session.ApplyResult.Files[0].Error = null;
        session.ApplyResult.Files[0].VerifiedAt = default(DateTimeOffset);
        session.Snapshots[0].Timestamp = default;
        session.Snapshots[0].Files[0].HashReadAt = default(DateTimeOffset);
        Program.Check(
            !Changed(session) && DiagnosticFileFindings.AfterUnlock(session)!.Severity == "Warning",
            "Default timestamps do not establish a verification boundary or a fresh final read"
        );

        session = Session();
        var snapshot = Snapshot(1, "GIF", "AfterUnlock");
        snapshot.Files[0].Path = Target.ToUpperInvariant();
        snapshot.Files.Add(
            new CacheFileEvidence
            {
                Path = @"C:\cache\LockScreen_A\LockScreen___1920_1080_notdimmed.jpg",
                Sha256 = "OTHER",
                Stable = true,
                HashSource = "Full read",
                HashReadAt = Boundary.AddSeconds(2),
            }
        );
        session.Snapshots.Add(snapshot);
        var variants = DiagnosticCacheFindings.Analyze(session).Single();
        var view = new DiagnosticFindingViewModel(variants);
        Program.Check(
            variants.Title == "Additional cache variants found"
                && variants.Severity == "Info"
                && variants.Children.All(child => child.Severity == "Info")
                && view.WarningVisibility == Visibility.Collapsed,
            "Optional variants and their child evidence stay informational; target path matching ignores case"
        );
    }

    private static bool Changed(DiagnosticSession session) =>
        DiagnosticCacheFindings.Analyze(session).Any(finding => finding.Title == "Cache contents changed after applying");

    private static bool Missing(DiagnosticSession session) =>
        DiagnosticCacheFindings.Analyze(session).Any(finding => finding.Title == "Verified files disappeared");

    private static DiagnosticSession Session() =>
        new()
        {
            LockObserved = true,
            UnlockObserved = true,
            Gif = new() { FrameCount = 4, Sha256 = "GIF" },
            ApplyResult = new()
            {
                Success = true,
                Files =
                [
                    new()
                    {
                        Path = Target,
                        Copied = true,
                        Verified = true,
                        VerifiedAt = Boundary,
                        Sha256 = "GIF",
                    },
                ],
            },
        };

    private static CacheSnapshot Snapshot(int seconds, string? hash, string reason) =>
        new()
        {
            Timestamp = Boundary.AddSeconds(seconds),
            Reason = reason,
            Files = hash is null
                ? []
                :
                [
                    new()
                    {
                        Path = Target,
                        Sha256 = hash,
                        Stable = true,
                        HashSource = "Full read",
                        HashReadAt = Boundary.AddSeconds(seconds + 0.5),
                    },
                ],
        };
}
