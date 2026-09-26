using LockscreenGif.Privileged;
using LockscreenGif.Privileged.Helper.Tracing;

namespace ProcessTracing.Tests;

internal static class ReuseTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "trace-reuse");
        var path = Path.Combine(root, "LockScreen.jpg");
        var other = Path.Combine(root, "LockScreen_new.jpg");
        var now = DateTimeOffset.UtcNow;
        var identity = new TraceIdentityMap(1, 2);
        identity.ProcessStart(9, 0, "Reader.exe", 1, now);
        var buffer = new TraceBuffer();
        var correlation = new FileOperationCorrelator(new(new(root, path)), identity, buffer);
        correlation.NameFile(1, path, now);
        correlation.Begin(1, 2, 1, "Read", 9, 0, now, 10);
        // Reuse the IRP for a filtered operation before the prior completion arrives.
        correlation.Begin(1, 2, 0, "Open", 9, 0, now.AddSeconds(1), rawPath: Path.Combine(Path.GetTempPath(), "unrelated.jpg"));
        correlation.End(1, 0, 10, now.AddSeconds(2));
        Program.Check(
            buffer.Drain().Operations.Single().Status is null,
            "A reused IRP for an unrelated file cannot complete the previous relevant read"
        );
        correlation.Begin(2, 2, 0, "Open", 9, 0, now.AddSeconds(3), rawPath: other);
        correlation.End(2, 0, 0, now.AddSeconds(4));
        correlation.Begin(3, 2, 1, "Read", 9, 0, now.AddSeconds(5), 10);
        correlation.End(3, 0, 10, now.AddSeconds(6));
        Program.Check(
            buffer.Drain().Operations.All(operation => operation.Path == other),
            "A reused object follows its newer open, not a stale key mapping"
        );
        correlation.Close(2);
        correlation.ForgetName(1);
        correlation.Begin(4, 2, 1, "Read", 9, 0, now.AddSeconds(7), 10);
        correlation.End(4, 0, 10, now.AddSeconds(8));
        correlation.NameFile(1, other, now.AddSeconds(9));
        correlation.End(999, 0, 10, now.AddSeconds(10));
        correlation.Finish();
        var batch = buffer.Drain();
        Program.Check(
            batch.Operations.Count == 0 && batch.Evidence.UnresolvedPaths > 0 && batch.Evidence.UnmatchedCompletions > 0,
            "Future handle mappings and unmatched completions remain explicit gaps"
        );
        var scope = new TracePathScope(new(root, path));
        Program.Check(
            scope.Normalize(@"\??\" + path) == path && scope.Normalize(@"\\?\" + path) == path,
            "NT and extended path prefixes normalize before scope filtering"
        );
        var retention = new TraceBuffer();
        for (var pid = 0; pid < 515; pid++)
        {
            retention.Add(new(now, path, "Read", pid, 1, "Reader.exe", 1, false, true, 1, 1, 0));
        }

        Program.Check(
            retention.Drain().Evidence is { Files.Count: 512, OmittedAggregates: 3 },
            "Per-file/process aggregate retention is bounded with an omission count"
        );
    }
}
