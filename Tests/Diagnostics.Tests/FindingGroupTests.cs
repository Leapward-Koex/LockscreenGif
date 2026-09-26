using System.Text.Json;
using LockscreenGif.Models;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Services.Diagnostics;

namespace Diagnostics.Tests;

internal static class FindingGroupTests
{
    internal static void Run()
    {
        var session = Session();
        var apply = session.ApplyResult!;
        apply.Files.Add(new() { Path = @"C:\cache\LockScreen_A\LockScreen_second_notdimmed.jpg", Error = "Access denied" });
        var group = DiagnosticFileFindings.Applied(apply, session.Gif!.Sha256);
        Program.Check(
            group.Severity == "Warning"
                && group.Children.Count == 2
                && group.Children[0].Severity == "Success"
                && group.Children[1].Severity == "Warning",
            "Partial file verification has a warning parent and independent per-file results"
        );
        apply.Cancelled = true;
        group = DiagnosticFileFindings.Applied(apply, session.Gif.Sha256);
        Program.Check(
            group.Title == "Applying was cancelled" && group.Severity == "Warning",
            "Cancelled apply remains a warning with per-file evidence"
        );
        apply.Cancelled = false;
        apply.Files.RemoveAt(1);
        group = DiagnosticFileFindings.Applied(apply, "");
        Program.Check(
            group.Severity == "Warning" && group.Children.All(child => child.Severity == "Warning"),
            "Missing expected hashes cannot produce successful file checks"
        );
        apply.Files[0].Sha256 = "unexpected";
        group = DiagnosticFileFindings.Applied(apply, session.Gif.Sha256);
        Program.Check(
            group.Severity == "Warning" && group.Children[0].Detail.Contains("unexpected"),
            "Hash mismatches override stale verified flags and expose the observed value"
        );
        apply.Files[0].Sha256 = "expected";
        group = DiagnosticFileFindings.Applied(apply, session.Gif.Sha256);
        Program.Check(
            group.Severity == "Success" && group.Children.Single().Severity == "Success",
            "Matching hash comparison ignores hexadecimal letter casing"
        );

        var final = new CacheSnapshot { Reason = "AfterUnlock" };
        final.Files.Add(
            new()
            {
                Path = apply.Files[0].Path,
                Stable = true,
                Sha256 = "EXPECTED",
                HashSource = "Full read",
                HashReadAt = final.Timestamp.AddSeconds(1),
            }
        );
        session.Snapshots.Add(final);
        group = DiagnosticFileFindings.AfterUnlock(session)!;
        Program.Check(
            group.Severity == "Success" && group.Children.Single().Severity == "Success",
            "Fresh final hashes produce successful parent and image checks"
        );
        final.Files[0].HashReadAt = final.Timestamp.AddSeconds(-1);
        group = DiagnosticFileFindings.AfterUnlock(session)!;
        Program.Check(
            group.Severity == "Warning" && group.Children[0].Severity == "Warning",
            "A stale final hash produces warnings on both group and image"
        );
        final.Files[0].HashReadAt = final.Timestamp.AddSeconds(1);
        final.Files[0].Stable = false;
        group = DiagnosticFileFindings.AfterUnlock(session)!;
        Program.Check(
            group.Children[0].Severity == "Warning" && group.Children[0].Detail.Contains("changed while"),
            "Unstable final reads remain unverified"
        );
        final.Files.Clear();
        final.Complete = false;
        group = DiagnosticFileFindings.AfterUnlock(session)!;
        Program.Check(
            group.Children[0].Detail.Contains("incomplete") && !group.Children[0].Detail.Contains("absent"),
            "Incomplete final inventories do not label unobserved images as missing"
        );
        final.Complete = true;
        group = DiagnosticFileFindings.AfterUnlock(session)!;
        Program.Check(
            group.Children[0].Detail.Contains("absent") && group.Severity == "Warning",
            "A complete inventory can flag an expected image as missing"
        );

        session = Session();
        final = new() { Reason = "AfterUnlock" };
        final.Files.Add(
            new()
            {
                Path = session.ApplyResult!.Files[0].Path,
                Stable = true,
                Sha256 = "OTHER",
                HashSource = "Full read",
                HashReadAt = final.Timestamp.AddSeconds(1),
            }
        );
        session.Snapshots.Add(final);
        var changed = DiagnosticAnalyzer.Analyze(session).Single(finding => finding.Title == "Cache contents changed after applying");
        Program.Check(
            changed.Children.Single().Severity == "Warning" && changed.Children[0].Detail.Contains("OTHER"),
            "Historical changes expand into the affected image and expected/observed hashes"
        );
        var clone = JsonSerializer.Deserialize<DiagnosticFinding>(JsonSerializer.Serialize(changed))!;
        Program.Check(clone.Children.Count == 1 && clone.Children[0].Severity == "Warning", "Grouped findings survive session persistence");

        session.Findings = [changed];
        var summary = DiagnosticReportWriter.CreateSummary(session);
        Program.Check(
            summary.Contains("LockScreen.jpg") && summary.Contains("Observed SHA-256: OTHER") && !summary.Contains(@"C:\cache"),
            "Copied summaries retain child evidence and redact its paths"
        );
        session.Observation = "Animated correctly";
        Program.Check(
            DiagnosticAnalyzer.Analyze(session).Single(f => f.Title == "Your observation").Severity == "Success",
            "A successful user observation has a check status"
        );
        session.Observation = "Could not tell";
        Program.Check(
            DiagnosticAnalyzer.Analyze(session).Single(f => f.Title == "Your observation").Severity == "Warning",
            "An uncertain user observation is not shown as success"
        );
    }

    private static DiagnosticSession Session() =>
        new()
        {
            LockObserved = true,
            UnlockObserved = true,
            Gif = new() { FrameCount = 4, Sha256 = "EXPECTED" },
            ApplyResult = new LockscreenApplyResult
            {
                Success = true,
                Files =
                [
                    new()
                    {
                        Path = @"C:\cache\LockScreen_A\LockScreen.jpg",
                        Copied = true,
                        Verified = true,
                        VerifiedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                        Sha256 = "EXPECTED",
                    },
                ],
            },
        };
}
