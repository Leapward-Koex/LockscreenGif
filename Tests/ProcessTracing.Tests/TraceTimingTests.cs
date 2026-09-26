using LockscreenGif.Privileged;
using LockscreenGif.Privileged.Helper.Tracing;

namespace ProcessTracing.Tests;

internal static class TraceTimingTests
{
    public static void Run()
    {
        var now = DateTimeOffset.UtcNow;
        const string path = @"C:\cache\LockScreen.jpg";
        var buffer = new TraceBuffer();
        TraceOperation Read(int second, uint? status = 0, long bytes = 32) =>
            new(
                now.AddSeconds(second),
                path,
                "Read",
                77,
                1,
                "Reader.exe",
                1,
                false,
                true,
                32,
                bytes,
                status,
                CompletedAt: now.AddSeconds(second + 1)
            );
        buffer.Add(Read(10));
        buffer.Add(Read(1));
        buffer.Add(Read(20, 0xc0000022, 0));
        buffer.Add(Read(21, null, 0));
        buffer.Add(Read(22, 0, 0));
        buffer.Add(Read(23) with { CompletedAt = null });
        buffer.Add(Read(24) with { CompletedAt = now });
        buffer.Add(Read(25) with { Operation = "Open", CompletedBytes = null });
        var aggregate = buffer.Drain().Evidence.Files.Single();
        Program.Check(
            aggregate.LastReadStartedAt == now.AddSeconds(10) && aggregate.LastReadCompletedAt == now.AddSeconds(11),
            "Read timing ignores late opens, failures, incomplete and out-of-order completions"
        );
        for (var i = 0; i < 4200; i++)
        {
            buffer.Add(Read(30 + i));
        }

        var latest = buffer.Drain().Evidence;
        Program.Check(
            latest.QueueDropped > 0 && latest.Files.Single().LastReadStartedAt == now.AddSeconds(4229),
            "Successful read timing survives transport overflow independently of raw records"
        );

        var root = Path.Combine(Path.GetTempPath(), "name-index-fixture");
        var relevant = Path.Combine(root, "LockScreen.jpg");
        var identities = new TraceIdentityMap(1, 2);
        identities.ProcessStart(77, 0, "Reader.exe", 1, now);
        var indexedBuffer = new TraceBuffer();
        var correlator = new FileOperationCorrelator(new(new(root, relevant)), identities, indexedBuffer);
        for (ulong i = 1; i <= 1000; i++)
        {
            correlator.Begin(i, i, i, "Read", 77, 0, now.AddSeconds(1), 1);
            correlator.End(i, 0, 1, now.AddSeconds(2));
        }
        for (ulong i = 2000; i < 12000; i++)
        {
            correlator.NameFile(i, relevant, now);
        }

        Program.Check(indexedBuffer.Drain().Operations.Count == 0, "Unrelated filename rundown does not resolve waiting operations");
        for (ulong i = 1; i <= 1000; i++)
        {
            correlator.NameFile(i, relevant, now);
        }

        var result = indexedBuffer.Drain();
        Program.Check(
            result.Evidence.Files.Single().Reads == 1000 && result.Evidence.UnresolvedPaths == 0,
            "Indexed filename resolution retains every completed waiting operation"
        );
        correlator.Finish();
        Program.Check(
            indexedBuffer.Drain().Evidence.Files.Single().Reads == 1000,
            "Resolved filename entries cannot emit operations twice during final draining"
        );
    }
}
