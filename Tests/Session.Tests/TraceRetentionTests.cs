using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Diagnostics;

namespace Session.Tests;

internal static class TraceRetentionTests
{
    public static async Task RunAsync()
    {
        await CheckLimitAsync("C:\\cache\\LockScreen.jpg", 10010, 10000);
        await CheckLimitAsync("C:\\cache\\" + new string('x', 4000), 5000, null);
    }

    private static async Task CheckLimitAsync(string path, int count, int? expected)
    {
        var now = DateTimeOffset.UtcNow;
        var helper = new FakePrivilegedSession();
        var stats = new ProcessTraceEvidence
        {
            State = "Recording",
            StartedAt = now,
            Files =
            [
                new()
                {
                    Path = path,
                    Reads = count,
                    ReadBytes = count,
                },
            ],
        };
        var operations = Enumerable
            .Range(0, count)
            .Select(_ => new TraceOperation(now, path, "Read", 5, 1, "Reader.exe", 1, false, true, 1, 1, 0))
            .ToList();
        helper.Batches.Enqueue(new(stats, operations, false));
        var recorder = new DiagnosticRecorder(new DiagnosticSession());
        var trace = new DiagnosticProcessTrace(helper, recorder);
        await trace.StartAsync(new("C:\\cache", path), default);
        var retained = recorder.Snapshot().ProcessTrace;
        Program.Check(
            retained.Operations.Count < count
                && retained.OmittedOperations == count - retained.Operations.Count
                && (expected is null || retained.Operations.Count == expected)
                && retained.Files.Single().Reads == count,
            expected is null
                ? "Trace byte budget omits records but preserves aggregates"
                : "Trace record limit is 10,000 with explicit omissions"
        );
        Program.Check(
            recorder.Snapshot(includeTraceDetails: false).ProcessTrace.Operations.Count == 0
                && recorder.Snapshot().ProcessTrace.Operations.Count == retained.Operations.Count,
            "Display snapshots omit raw trace operations without changing report evidence"
        );
        stats.State = "Completed";
        helper.Batches.Enqueue(new(stats, [], false));
        await trace.FinishAsync();
        Program.Check(
            recorder.Snapshot().ProcessTrace.State == "Incomplete",
            "Retained-operation omissions remain visible after final draining"
        );
    }
}
