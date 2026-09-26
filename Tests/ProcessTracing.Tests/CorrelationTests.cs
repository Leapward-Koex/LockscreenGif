using LockscreenGif.Privileged;
using LockscreenGif.Privileged.Helper.Tracing;

namespace ProcessTracing.Tests;

internal static class CorrelationTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "trace-correlation");
        var file = Path.Combine(root, "LockScreen.jpg");
        var buffer = new TraceBuffer();
        var identity = new TraceIdentityMap(1, 2);
        var start = DateTimeOffset.UtcNow;
        identity.ProcessStart(1, 0, "App.exe", 1, start);
        identity.ProcessStart(40, 0, "Reader.exe", 1, start);
        identity.ThreadStart(60, 40, start);
        var correlator = new FileOperationCorrelator(new(new(root, file)), identity, buffer);
        correlator.NameFile(1, file, start);
        correlator.Begin(10, 2, 1, "Read", -1, 60, start.AddMilliseconds(1), 100);
        Program.Check(buffer.Drain().Operations.Count == 0, "An attempted read is not a completed read");
        correlator.End(10, 0, 100, start.AddMilliseconds(2));
        var batch = buffer.Drain();
        Program.Check(
            batch.Operations.Single() is { ProcessId: 40, AttributionResolved: true, IsApp: false, CompletedBytes: 100 }
                && batch.Evidence.Files.Single().Reads == 1,
            "Completion resolves issuing thread and exact byte count"
        );
        correlator.Begin(11, 2, 1, "Read", 1, 0, start.AddMilliseconds(3), 50);
        correlator.End(11, 0, 50, start.AddMilliseconds(4));
        Program.Check(buffer.Drain().Operations.Single().IsApp, "App verification reads are explicitly tagged");
        correlator.Begin(12, 2, 1, "Write", 40, 60, start.AddMilliseconds(5), 50);
        correlator.End(12, 0xc0000022, 0, start.AddMilliseconds(6));
        batch = buffer.Drain();
        Program.Check(
            !batch.Operations.Single().Succeeded && batch.Evidence.Files.Single(f => f.ProcessId == 40).Failures == 1,
            "Access denied cannot become successful modification"
        );
        correlator.End(13, 0, 22, start.AddMilliseconds(8));
        correlator.Begin(13, 2, 1, "Read", 40, 60, start.AddMilliseconds(7), 22);
        Program.Check(buffer.Drain().Operations.Single().CompletedBytes == 22, "Out-of-order completion can join its operation");
        correlator.Begin(14, 3, 4, "Read", 40, 60, start.AddMilliseconds(9), 33);
        correlator.End(14, 0, 33, start.AddMilliseconds(11));
        correlator.NameFile(4, file, start.AddMilliseconds(8));
        Program.Check(buffer.Drain().Operations.Single().CompletedBytes == 33, "Late filename resolves a bounded pending operation");
        correlator.Begin(15, 2, 1, "Read", 40, 60, start.AddMilliseconds(12), 20);
        identity.ProcessEnd(40);
        identity.ProcessStart(40, 0, "Reused.exe", 1, start.AddMilliseconds(13));
        correlator.End(15, 0, 20, start.AddMilliseconds(14));
        Program.Check(buffer.Drain().Operations.Single().ProcessName == "Reader.exe", "In-flight operation retains original PID lifetime");
        correlator.Begin(16, 2, 1, "Read", -1, 60, start.AddMilliseconds(15), 1);
        correlator.End(16, 0, 1, start.AddMilliseconds(16));
        Program.Check(!buffer.Drain().Operations.Single().AttributionResolved, "Stale thread mapping cannot identify a reused process");
        correlator.Begin(17, 8, 0, "Open", 40, 0, start.AddMilliseconds(17), rawPath: Path.Combine(root, "..", "unrelated.jpg"));
        correlator.End(17, 0, 0, start.AddMilliseconds(18));
        Program.Check(buffer.Drain().Operations.Count == 0, "Unrelated paths are discarded before transport");
        correlator.Begin(18, 2, 1, "Read", 40, 0, start.AddMilliseconds(19), 1);
        correlator.Finish();
        Program.Check(buffer.Drain().Operations.Single().Status is null, "Missing completion remains inconclusive");
        for (var i = 0; i < 4100; i++)
        {
            buffer.Add(new(start, file, "Read", 3, 1, "Other.exe", 1, false, true, 1, 1, 0));
        }

        batch = buffer.Drain();
        Program.Check(
            batch.Evidence.QueueDropped == 4 && batch.Evidence.Files.Single(f => f.ProcessId == 3).Reads == 4100,
            "Transport overflow is counted while bounded aggregates continue"
        );
        var endings = new TraceBuffer();
        var eof = new TraceOperation(start, file, "Read", 40, 1, "Reader.exe", 1, false, true, 1, 0, 0xc0000011);
        endings.Add(eof);
        var ended = endings.Drain();
        Program.Check(
            ended.Operations.Single().Status == 0xc0000011
                && eof.IsEndOfFile
                && !eof.Succeeded
                && ended.Evidence.Files.Single().Failures == 0
                && ended.Evidence.Files.Single().Reads == 0,
            "EOF stays in raw evidence without counting as an access failure or successful data read"
        );
        endings.Add(eof with { Operation = "Write" });
        Program.Check(endings.Drain().Evidence.Files.Single().Failures == 1, "The benign EOF rule does not hide a write error");
        var unscoped = new ProcessTraceEvidence { UnresolvedPaths = 21886, UnmatchedCompletions = 342 };
        Program.Check(
            unscoped.HasGaps && !unscoped.HasCollectionGaps && unscoped.HasUnresolvedActivity,
            "Unscoped system activity remains uncertainty, not confirmed collection loss"
        );
        var scope = new TracePathScope(new(root, file));
        Program.Check(
            scope.Contains(file.ToUpperInvariant()) && !scope.Contains(root + "-other\\file.jpg"),
            "Scope matches a directory boundary case-insensitively"
        );
    }
}
