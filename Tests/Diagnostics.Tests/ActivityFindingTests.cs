using System.Text.Json;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Diagnostics;
using LockscreenGif.ViewModels;
using Microsoft.UI.Xaml;

namespace Diagnostics.Tests;

internal static class ActivityFindingTests
{
    private const string Path = @"C:\cache\LockScreen_A\LockScreen.jpg";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    public static void Run()
    {
        SuccessfulCycle(11, 43495);
        SuccessfulCycle(14, 518862);
        PhaseBoundaries();
        LegacyCoverage();
        IdentityAndAttribution();
        MissingAggregates();
        SummaryRoundTrip();
    }

    private static DiagnosticSession Session() =>
        new()
        {
            ApplyResult = new()
            {
                Success = true,
                Files =
                [
                    new()
                    {
                        Path = Path,
                        Copied = true,
                        Verified = true,
                        VerifiedAt = Start.AddSeconds(5),
                        Sha256 = "APPLIED",
                    },
                ],
            },
            ProcessTrace = new()
            {
                State = "Completed",
                StartedAt = Start,
                EndedAt = Start.AddSeconds(20),
            },
        };

    private static TraceAggregate Aggregate() =>
        new()
        {
            Path = Path,
            ProcessName = "Reader.exe",
            ProcessId = 77,
            ProcessInstance = 2,
            SessionId = 3,
            AttributionResolved = true,
            FastIoFallbacks = 0,
        };

    private static TraceOperation Operation(double start, double? completion, uint status = 0xc0000022, string operation = "Open") =>
        new(
            Start.AddSeconds(start),
            Path,
            operation,
            77,
            2,
            "Reader.exe",
            3,
            false,
            true,
            64,
            status == 0 ? 64 : 0,
            status,
            CompletedAt: completion is { } seconds ? Start.AddSeconds(seconds) : null
        );

    private static void SuccessfulCycle(int variants, long bytes)
    {
        var session = Session();
        session.ApplyResult!.Files = Enumerable
            .Range(0, variants)
            .Select(index => new LockscreenGif.Models.LockscreenFileResult
            {
                Path = $@"C:\cache\LockScreen_{index}\LockScreen.jpg",
                Copied = true,
                Verified = true,
                VerifiedAt = Start.AddSeconds(5),
                Sha256 = "APPLIED",
            })
            .ToList();
        foreach (var target in session.ApplyResult.Files)
        {
            var setup = Aggregate();
            setup.Path = target.Path;
            setup.Failures = 2;
            setup.FailureTiming = new TraceActivityTiming().Add(Operation(1, 1.01)).Add(Operation(2, 2.01, 0xc0000034));
            setup.FastIoFallbacks = 1;
            setup.Modifications = 1;
            setup.ModificationTiming = new TraceActivityTiming().Add(Operation(3, 3.01, 0, "Write"));
            // A later unrelated operation must not change the phase of earlier failures.
            setup.LastAt = Start.AddSeconds(19);
            session.ProcessTrace.Files.Add(setup);
            session.ProcessTrace.Operations.Add(Operation(2.5, 2.51, 0xc01c0004, "Write") with { Path = target.Path });
            var system = Aggregate();
            system.Path = target.Path;
            system.ProcessId = 4;
            system.ProcessName = "System";
            system.Modifications = 1;
            system.ModificationTiming = new TraceActivityTiming().Add(Operation(10, 10.01, 0, "Write"));
            session.ProcessTrace.Files.Add(system);
        }

        var reader = Aggregate();
        reader.Path = session.ApplyResult.Files[^1].Path;
        reader.ProcessName = "LogonUI.exe";
        reader.Reads = 2;
        reader.ReadBytes = bytes;
        reader.LastReadStartedAt = Start.AddSeconds(11);
        reader.LastReadCompletedAt = Start.AddSeconds(11.01);
        session.ProcessTrace.Files.Add(reader);
        var findings = DiagnosticTraceFindings.Analyze(session).ToArray();
        Program.Check(
            findings.All(finding => finding.Severity != "Warning"),
            $"Successful {variants}-variant cycle does not warn on setup or System activity"
        );
        var other = findings.Single(finding => finding.Title == "Other file activity");
        Program.Check(
            other.Severity == "Info" && other.Children.Count == variants && other.Children.All(child => child.Severity == "Info"),
            "Routine evidence is one informational group with per-file children"
        );
        Program.Check(
            other.Children.All(child =>
                child.Detail.Contains("initiating application unknown")
                && child.Detail.Contains("whole test")
                && child.Detail.Contains("0xC01C0004")
            ),
            "System attribution, whole-test totals and raw fallback status remain visible"
        );
        var view = new DiagnosticFindingViewModel(other);
        Program.Check(
            !view.IsExpanded && view.WarningVisibility == Visibility.Collapsed && view.InformationVisibility == Visibility.Visible,
            "Other file activity starts collapsed and its informational children do not promote warning severity"
        );
        Program.Check(
            findings.Single(finding => finding.Title == "External image reads").Severity == "Success",
            "Successful cycles retain independently observed file access"
        );
    }

    private static void PhaseBoundaries()
    {
        var session = Session();
        var file = Aggregate();
        file.Failures = 1;
        file.LastAt = Start.AddSeconds(20);
        session.ProcessTrace.Files = [file];
        DiagnosticActivityClassification Timing() => DiagnosticActivityTiming.Classify(session, file, DiagnosticActivityKind.Failure);
        file.FailureTiming = new TraceActivityTiming().Add(Operation(1, 2));
        Program.Check(
            Timing().Phase == DiagnosticActivityPhase.BeforeVerification,
            "Failure phase ignores the aggregate LastAt of unrelated later activity"
        );
        file.FailureTiming = file.FailureTiming with { LatestStarted = null };
        Program.Check(Timing().Phase == DiagnosticActivityPhase.Unknown, "An incomplete summary cannot establish all-before timing");
        file.FailureTiming = new TraceActivityTiming().Add(Operation(4, 6));
        Program.Check(
            Timing().Phase == DiagnosticActivityPhase.OverlapsVerification,
            "A failure crossing verification remains inconclusive"
        );
        file.FailureTiming = new TraceActivityTiming().Add(Operation(5, 6));
        Program.Check(
            Timing().Phase == DiagnosticActivityPhase.AfterVerification,
            "An operation starting at verification qualifies for the applied generation"
        );
        file.FailureTiming = new TraceActivityTiming().Add(Operation(12, 12.01)).Add(Operation(1, 1.01));
        file.LastFailure = 0xc0000034;
        file.LastFailedOperation = "Read";
        file.LastFailureDescription = "An unrelated older result";
        file.Reads = 1;
        file.LastReadStartedAt = Start.AddSeconds(15);
        file.LastReadCompletedAt = Start.AddSeconds(15.01);
        var findings = DiagnosticTraceFindings.Analyze(session).ToArray();
        var failure = findings.Single(finding => finding.Title == "Image access failures");
        Program.Check(
            failure.Severity == "Warning"
                && failure.Children[0].Detail.Contains("Open, status 0xC0000022")
                && failure.Children[0].Detail.Contains("session 3")
                && failure.Children[0].Detail.Contains("00:00:12"),
            "A genuine post-verification attempt warns using its exact status, operation, timing and identity"
        );
        Program.Check(
            findings.Single(finding => finding.Title == "External image reads").Severity == "Success",
            "A later successful read does not erase a genuine failed attempt"
        );
        file.FailureTiming = new TraceActivityTiming().Add(Operation(1, 2)).Add(Operation(3, null));
        Program.Check(
            Timing().Phase == DiagnosticActivityPhase.Unknown,
            "An untimed operation prevents claiming every failure preceded verification"
        );
        file.FailureTiming = new TraceActivityTiming().Add(Operation(12, 12.01)).Add(Operation(3, null));
        session.ProcessTrace.OmittedOperations = 2;
        Program.Check(
            Timing().Phase == DiagnosticActivityPhase.AfterVerification,
            "Positive bounded evidence survives raw retention omissions and untimed attempts"
        );
        file.FailureTiming = new TraceActivityTiming().Add(Operation(1, 2));
        Program.Check(
            Timing().Phase == DiagnosticActivityPhase.BeforeVerification
                && DiagnosticTraceFindings.Analyze(session).First().Severity == "Warning",
            "Summary covers observed earlier activity despite omitted details while collection loss separately warns"
        );
        session.ApplyResult!.Files[0].VerifiedAt = null;
        Program.Check(Timing().Phase == DiagnosticActivityPhase.Unknown, "A missing verification boundary is unknown");
        session.ApplyResult.Files[0].VerifiedAt = Start.AddSeconds(5);
        session.ApplyResult.Files[0].Error = "Verification failed";
        Program.Check(Timing().Phase == DiagnosticActivityPhase.Unknown, "A timestamp from an unsuccessful verification is not a boundary");
    }

    private static void LegacyCoverage()
    {
        var session = Session();
        var file = Aggregate();
        file.FastIoFallbacks = null;
        file.Failures = 2;
        session.ProcessTrace.Files = [file];
        session.ProcessTrace.Operations = [Operation(1, 2), Operation(2, 3, 0xc01c0004, "Write")];
        DiagnosticActivityClassification Timing() => DiagnosticActivityTiming.Classify(session, file, DiagnosticActivityKind.Failure);
        Program.Check(
            Timing().Phase == DiagnosticActivityPhase.BeforeVerification,
            "Legacy failure reconciliation includes fallback in the old total but excludes it from failure witnesses"
        );
        file.Failures++;
        Program.Check(
            Timing().Phase == DiagnosticActivityPhase.Unknown,
            "Unreconciled legacy category totals cannot establish all-before timing"
        );
        file.Failures--;
        session.ProcessTrace.QueueDropped = 1;
        Program.Check(
            Timing().Phase == DiagnosticActivityPhase.Unknown,
            "Legacy raw evidence cannot establish all-before timing when collection has a gap"
        );
        session.ProcessTrace.Operations[0] = Operation(7, 8);
        Program.Check(
            Timing().Phase == DiagnosticActivityPhase.AfterVerification,
            "A retained legacy post-verification failure is positive evidence even with a gap"
        );
        session.ProcessTrace.QueueDropped = 0;
        session.ProcessTrace.State = "Incomplete";
        session.ProcessTrace.Operations[0] = Operation(1, 2);
        Program.Check(Timing().Phase == DiagnosticActivityPhase.Unknown, "Legacy incomplete tracing cannot establish all-before timing");
        session.ProcessTrace.State = "Completed";
        session.ProcessTrace.Operations[0] = Operation(1, null);
        Program.Check(
            Timing().Phase == DiagnosticActivityPhase.Unknown,
            "A legacy failure without completion cannot be assigned to an earlier phase"
        );
        session.ProcessTrace.Operations = [Operation(1, 2), Operation(2, null, 0xc01c0004, "Write")];
        Program.Check(
            Timing().Phase == DiagnosticActivityPhase.Unknown,
            "Legacy all-before classification also requires timing on fallback records included in its failure total"
        );
        session.ProcessTrace.Operations = [Operation(6, 7, 0xc01c0004, "Write")];
        file.Failures = 1;
        var findings = DiagnosticTraceFindings.Analyze(session).ToArray();
        Program.Check(
            findings.All(finding => finding.Severity != "Warning")
                && findings.Single(finding => finding.Title == "Other file activity").Children[0].Detail.Contains("Fast I/O fallback"),
            "A fallback-only legacy aggregate does not create an access-failure warning"
        );
    }

    private static void IdentityAndAttribution()
    {
        var session = Session();
        var file = Aggregate();
        file.Failures = 1;
        session.ProcessTrace.Files = [file];
        session.ProcessTrace.Operations = [Operation(7, 8) with { Path = Path.ToLowerInvariant() }];
        Program.Check(
            DiagnosticActivityTiming.Classify(session, file, DiagnosticActivityKind.Failure).Phase
                == DiagnosticActivityPhase.AfterVerification,
            "Raw matching treats path casing as equivalent"
        );
        session.ProcessTrace.Operations[0] = session.ProcessTrace.Operations[0] with { ProcessInstance = 3 };
        Program.Check(
            DiagnosticActivityTiming.Classify(session, file, DiagnosticActivityKind.Failure).Phase == DiagnosticActivityPhase.Unknown,
            "PID reuse cannot assign another process lifetime's evidence to an aggregate"
        );
        session.ProcessTrace.Operations[0] = Operation(7, 8) with { ProcessId = 78 };
        Program.Check(
            DiagnosticActivityTiming.Classify(session, file, DiagnosticActivityKind.Failure).Phase == DiagnosticActivityPhase.Unknown,
            "Another process cannot supply timing for an aggregate"
        );
        file.FailureTiming = new TraceActivityTiming().Add(Operation(7, 8));
        session.ProcessTrace.Operations.Clear();
        file.ProcessId = 4;
        file.ProcessName = "System";
        var findings = DiagnosticTraceFindings.Analyze(session).ToArray();
        Program.Check(
            findings.All(finding => finding.Severity != "Warning")
                && findings
                    .Single(finding => finding.Title == "Other file activity")
                    .Children[0]
                    .Detail.Contains("initiating application unknown"),
            "System failures do not identify an independent application or warn by themselves"
        );
        file.ProcessId = 77;
        file.AttributionResolved = false;
        Program.Check(
            DiagnosticTraceFindings.Analyze(session).All(finding => finding.Severity != "Warning"),
            "Unknown process attribution is information"
        );
        file.AttributionResolved = true;
        file.Path = @"C:\unrelated.gif";
        Program.Check(
            DiagnosticTraceFindings.Analyze(session).All(finding => finding.Severity != "Warning"),
            "Failure on a path outside intended apply targets does not warn about the verified GIF"
        );
        file.Path = Path;
        file.IsApp = true;
        Program.Check(
            DiagnosticTraceFindings
                .Analyze(session)
                .All(finding => finding.Title != "Image access failures" && finding.Title != "Other file activity"),
            "The app's own probes remain raw evidence without redundant trace findings"
        );
    }

    private static void MissingAggregates()
    {
        var session = Session();
        session.ProcessTrace.OmittedAggregates = 1;
        session.ProcessTrace.Operations = [Operation(7, 8)];
        var findings = DiagnosticTraceFindings.Analyze(session).ToArray();
        var failure = findings.Single(finding => finding.Title == "Image access failures");
        Program.Check(
            findings.First().Severity == "Warning"
                && failure.Severity == "Warning"
                && failure.Children.Single().Detail.Contains("1 failed operation(s), retained")
                && failure.Children.Single().Detail.Contains("Open, status 0xC0000022"),
            "A retained post-verification failure warns alongside the collection gap when its aggregate was omitted"
        );
        Program.Check(session.ProcessTrace.Files.Count == 0, "Transient raw evidence views do not alter exported aggregates or counters");
        var aggregate = Aggregate();
        aggregate.Failures = 2;
        session.ProcessTrace.Files = [aggregate];
        failure = DiagnosticTraceFindings.Analyze(session).Single(finding => finding.Title == "Image access failures");
        Program.Check(
            failure.Children.Single().Detail.Contains("2 failed operation(s), whole test")
                && !failure.Children.Single().Detail.Contains("retained"),
            "Retained operations do not duplicate a matching whole-test aggregate"
        );
        session.ProcessTrace.Files.Clear();
        session.ProcessTrace.OmittedAggregates = 0;
        session.ProcessTrace.Operations = [Operation(1, 2)];
        var routine = DiagnosticTraceFindings.Analyze(session).Single(finding => finding.Title == "Other file activity");
        Program.Check(
            routine.Children[0].Detail.Contains("Timing relative to verification is unknown"),
            "A raw-only group cannot establish that all activity preceded verification even when an omission counter is absent"
        );
        session.ProcessTrace.Operations = [Operation(7, 8, 0, "Read") with { AttributionResolved = false }];
        Program.Check(
            DiagnosticTraceFindings.Analyze(session).Single(finding => finding.Title == "External image reads").Severity == "Info",
            "A raw-only read without resolved process attribution cannot establish an independent reader"
        );
        session.ProcessTrace.Operations.Add(Operation(8, 9, 0, "Read"));
        Program.Check(
            DiagnosticTraceFindings.Analyze(session).Single(finding => finding.Title == "External image reads").Severity == "Info",
            "Mixed raw-only process attribution is kept inconclusive"
        );
        session.ProcessTrace.Operations = [Operation(7, 8, 0, "Read")];
        Program.Check(
            DiagnosticTraceFindings.Analyze(session).Single(finding => finding.Title == "External image reads").Severity == "Success",
            "A resolved retained application read is positive evidence even without a whole-test aggregate"
        );
    }

    private static void SummaryRoundTrip()
    {
        var session = Session();
        var file = Aggregate();
        file.Failures = 1;
        file.FastIoFallbacks = 2;
        file.FailureTiming = new TraceActivityTiming().Add(Operation(7, 8));
        file.Modifications = 1;
        file.ModificationTiming = new TraceActivityTiming().Add(Operation(9, 10, 0, "Write"));
        session.ProcessTrace.Files = [file];
        var roundTrip = JsonSerializer.Deserialize<DiagnosticSession>(JsonSerializer.Serialize(session))!;
        var restored = roundTrip.ProcessTrace.Files[0];
        Program.Check(
            roundTrip.SchemaVersion == 2
                && restored.FailureTiming == file.FailureTiming
                && restored.ModificationTiming == file.ModificationTiming
                && restored.FastIoFallbacks == 2,
            "Schema-2 JSON round trips bounded timings and separate fallback counts"
        );
        Program.Check(
            DiagnosticTraceFindings.Analyze(roundTrip).Single(finding => finding.Title == "Image access failures").Severity == "Warning",
            "Round-tripped summaries retain post-verification warning evidence without raw operations"
        );
        var old = JsonSerializer.Deserialize<TraceAggregate>("{\"Failures\":1}")!;
        Program.Check(
            old.FailureTiming is null && old.ModificationTiming is null && old.FastIoFallbacks is null,
            "Missing additive fields in older exports stay unknown"
        );
    }
}
