using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Diagnostics;

namespace Diagnostics.Tests;

internal static class ReadTimingTests
{
    public static void Run()
    {
        var now = DateTimeOffset.UtcNow;
        const string path = @"C:\cache\LockScreen_C\LockScreen.jpg";
        var session = new DiagnosticSession
        {
            ApplyResult = new()
            {
                Files =
                [
                    new()
                    {
                        Path = path,
                        Copied = true,
                        Verified = true,
                        VerifiedAt = now.AddSeconds(5),
                    },
                ],
            },
            ProcessTrace = new() { State = "Completed", StartedAt = now },
        };
        var file = session.ApplyResult.Files[0];
        var read = new TraceAggregate
        {
            Path = path,
            ProcessId = 4,
            ProcessName = "System",
            AttributionResolved = true,
            Reads = 2228,
            ReadBytes = 145966787,
            LastReadStartedAt = now.AddSeconds(3),
            LastReadCompletedAt = now.AddSeconds(3.01),
        };
        session.ProcessTrace.Files = [read];
        DiagnosticFinding Finding() => DiagnosticTraceFindings.Analyze(session).Single(f => f.Title == "External image reads");
        Program.Check(
            Finding().Severity == "Info" && Finding().Children[0].Severity == "Info",
            "Large baseline System reads do not earn a checkmark for the new GIF"
        );
        read.ProcessId = 77;
        read.ProcessName = "Reader.exe";
        // An unrelated open at the end of the trace must not turn an old read into a new read.
        read.LastAt = now.AddSeconds(20);
        Program.Check(Finding().Severity == "Info", "Application reads of previous contents remain inconclusive");
        read.LastReadCompletedAt = now.AddSeconds(6);
        Program.Check(Finding().Severity == "Info", "Reads beginning before verification cannot pass by finishing later");
        read.LastReadStartedAt = now.AddSeconds(10);
        read.LastReadCompletedAt = now.AddSeconds(10.01);
        Program.Check(
            Finding().Severity == "Success" && !Finding().Children[0].Detail.Contains("2228"),
            "Later successful read passes without relabeling whole-trace totals as new GIF reads"
        );
        session.ProcessTrace.QueueDropped = 10;
        Program.Check(
            Finding().Severity == "Success" && session.ProcessTrace.Operations.Count == 0,
            "Read timing aggregates retain positive evidence even when raw records are missing"
        );
        read.ProcessId = 4;
        Program.Check(
            Finding().Severity == "Info" && Finding().Children[0].Detail.Contains("System I/O"),
            "System-only activity after apply can be inspection and does not prove an independent reader"
        );
        read.ProcessId = 77;
        file.VerifiedAt = null;
        Program.Check(Finding().Severity == "Info", "Missing verification time is not guessed from file names");
        file.VerifiedAt = now.AddSeconds(5);
        read.LastReadStartedAt = null;
        Program.Check(Finding().Severity == "Info", "Older aggregates without read timestamps cannot pass");
    }
}
