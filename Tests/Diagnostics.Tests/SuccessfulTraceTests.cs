using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Diagnostics;
using LockscreenGif.ViewModels;
using Microsoft.UI.Xaml;

namespace Diagnostics.Tests;

internal static class SuccessfulTraceTests
{
    public static void Run()
    {
        var now = DateTimeOffset.UtcNow;
        var paths = Enumerable.Range(0, 11).Select(index => $"C:\\cache\\LockScreen_{index}\\LockScreen.jpg").ToArray();
        var session = new DiagnosticSession
        {
            ApplyResult = new()
            {
                Success = true,
                Files = paths
                    .Select(path => new LockscreenGif.Models.LockscreenFileResult
                    {
                        Path = path,
                        Copied = true,
                        Verified = true,
                        VerifiedAt = now.AddSeconds(5),
                    })
                    .ToList(),
            },
            ProcessTrace = new()
            {
                State = "Completed",
                StartedAt = now,
                EndedAt = now.AddSeconds(20),
                UnmatchedCompletions = 342,
                UnresolvedPaths = 21886,
            },
        };
        var trace = session.ProcessTrace;
        // Reproduce the successful report: one LogonUI read and noisy app/helper probes.
        trace.Files =
        [
            new()
            {
                Path = paths[^1],
                ProcessName = "LogonUI.exe",
                ProcessId = 67844,
                AttributionResolved = true,
                Reads = 2,
                ReadBytes = 43495,
                LastReadStartedAt = now.AddSeconds(10),
                LastReadCompletedAt = now.AddSeconds(10.01),
            },
            new()
            {
                Path = paths[0],
                ProcessName = "LockscreenGif",
                IsApp = true,
                AttributionResolved = true,
                Failures = 11,
                LastFailure = 0xc0000011,
                LastFailedOperation = "Read",
            },
            new()
            {
                Path = paths[1],
                ProcessName = "icacls.exe",
                IsApp = true,
                AttributionResolved = true,
                Failures = 2,
                LastFailure = 0xc0000022,
                LastFailedOperation = "Open",
            },
            new()
            {
                Path = paths[2],
                ProcessName = "LockscreenGif.Privileged.Helper",
                IsApp = true,
                AttributionResolved = true,
                Failures = 32,
                LastFailure = 0xc0000034,
                LastFailedOperation = "Open",
            },
        ];
        var findings = DiagnosticTraceFindings.Analyze(session).ToList();
        var reads = findings.Single(finding => finding.Title == "External image reads");
        Program.Check(
            reads.Severity == "Success"
                && reads.Children.Count(child => child.Severity == "Success") == 1
                && reads.Children.Count(child => child.Severity == "Info") == 10
                && reads.Children[0].Title == paths[^1],
            "One of eleven GIF copies being read passes and appears first"
        );
        Program.Check(
            findings.All(finding => finding.Severity != "Warning") && findings.All(finding => finding.Title != "Image access failures"),
            "Successful run has no warnings from optional variants, unscoped events or app/helper probes"
        );
        Program.Check(
            trace.HasGaps && !trace.HasCollectionGaps && trace.Files[1].Failures == 11,
            "Technical uncertainty and app failures remain in exported evidence"
        );
        var view = new DiagnosticFindingViewModel(reads);
        Program.Check(
            view.SuccessVisibility == Visibility.Visible
                && view.WarningVisibility == Visibility.Collapsed
                && view.Children.Skip(1).All(child => child.InformationVisibility == Visibility.Visible),
            "Optional image entries have neutral icons without turning a passing group yellow"
        );
        var failedChild = new DiagnosticFindingViewModel(
            new("Group", "", "Success") { Children = [new("C:\\cache\\LockScreen.jpg", "Access denied", "Warning")] }
        );
        Program.Check(failedChild.WarningVisibility == Visibility.Visible, "An actual failed child still warns at group level");
        trace.Files[0].Path = "C:\\unrelated-source.gif";
        reads = DiagnosticTraceFindings.Analyze(session).Single(finding => finding.Title == "External image reads");
        Program.Check(
            reads.Severity == "Info" && reads.Confidence == "Inconclusive",
            "Reading another file cannot pass the applied-GIF access check"
        );
        trace.Files[0].Path = paths[^1];
        session.ApplyResult.Files[^1].Verified = false;
        Program.Check(
            DiagnosticTraceFindings.Analyze(session).Single(finding => finding.Title == "External image reads").Severity == "Info",
            "A read of an unverified copy does not establish access to the selected GIF"
        );
        session.ApplyResult.Files[^1].Verified = true;
        trace.QueueDropped = 1;
        Program.Check(
            DiagnosticTraceFindings.Analyze(session).First().Severity == "Warning",
            "Real transport loss still warns even when one GIF read succeeded"
        );
        trace.QueueDropped = 0;
        trace.State = "Incomplete";
        trace.Reason = "Helper disconnected.";
        Program.Check(
            DiagnosticTraceFindings.Analyze(session).First().Severity == "Warning",
            "Early tracing termination is never hidden by a successful read"
        );
        trace.State = "Completed";
        trace.Reason = null;
        trace.Files.Add(
            new()
            {
                Path = paths[0],
                ProcessName = "Other.exe",
                ProcessId = 77,
                AttributionResolved = true,
                Failures = 1,
                FastIoFallbacks = 0,
                FailureTiming = new(
                    new(now.AddSeconds(12), now.AddSeconds(12.01), "Open", 0xc0000022),
                    new(now.AddSeconds(12), now.AddSeconds(12.01), "Open", 0xc0000022)
                ),
                LastFailure = 0xc0000022,
                LastFailedOperation = "Open",
            }
        );
        Program.Check(
            DiagnosticTraceFindings.Analyze(session).Single(finding => finding.Title == "Image access failures").Severity == "Warning",
            "Independent post-verification access-denied attempts still warn"
        );
    }
}
