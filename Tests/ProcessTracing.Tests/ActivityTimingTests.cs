using System.Text.Json;
using LockscreenGif.Privileged;
using LockscreenGif.Privileged.Helper.Tracing;

namespace ProcessTracing.Tests;

internal static class ActivityTimingTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        FastIoFallback();
        FailureOrdering();
        ModificationOrdering();
        TiesAndMissingTiming();
        OverflowAndSerialization();
        MaximumBatchSize();
    }

    private static TraceOperation Operation(int started, int completed, string operation, uint status = 0) =>
        new(
            Start.AddSeconds(started),
            @"C:\cache\LockScreen.jpg",
            operation,
            77,
            1,
            "Reader.exe",
            1,
            false,
            true,
            32,
            status == 0 ? 32 : 0,
            status,
            CompletedAt: Start.AddSeconds(completed)
        );

    private static void FastIoFallback()
    {
        var buffer = new TraceBuffer();
        foreach (var name in new[] { "Read", "Write", "Open" })
        {
            var operation = Operation(1, 2, name, 0xc01c0004);
            Program.Check(
                operation.IsFastIoFallback && !operation.IsFailure && !operation.Succeeded,
                $"Fast-I/O fallback remains neither a terminal failure nor a successful {name}"
            );
            buffer.Add(operation);
        }

        var batch = buffer.Drain();
        var aggregate = batch.Evidence.Files.Single();
        Program.Check(
            aggregate.FastIoFallbacks == 3
                && aggregate.Failures == 0
                && aggregate.Reads == 0
                && aggregate.Modifications == 0
                && aggregate.FailureTiming == new TraceActivityTiming()
                && aggregate.ModificationTiming == new TraceActivityTiming()
                && batch.Operations.All(operation => operation.Status == 0xc01c0004),
            "Fallback is counted separately while its original raw statuses remain available"
        );
        buffer.Add(Operation(3, 4, "Read"));
        Program.Check(
            buffer.Drain().Evidence.Files.Single().Reads == 1,
            "A subsequent successful read establishes its own data-access evidence"
        );
    }

    private static void FailureOrdering()
    {
        var buffer = new TraceBuffer();
        buffer.Add(Operation(10, 11, "Read", 0xc0000022));
        var first = buffer.Drain().Evidence.Files.Single();
        buffer.Add(Operation(1, 20, "Open", 0xc0000034));
        var aggregate = buffer.Drain().Evidence.Files.Single();
        Program.Check(
            aggregate.FailureTiming!.LatestStarted is { Operation: "Read", Status: 0xc0000022 }
                && aggregate.FailureTiming.LatestStarted.StartedAt == Start.AddSeconds(10)
                && aggregate.FailureTiming.LatestCompleted is { Operation: "Open", Status: 0xc0000034 }
                && aggregate.FailureTiming.LatestCompleted.CompletedAt == Start.AddSeconds(20)
                && aggregate.LastFailure == 0xc0000022
                && aggregate.LastFailedOperation == "Read",
            "Late-arriving older failures retain separate latest-start and latest-completion witnesses with matching status"
        );
        var boundary = Start.AddSeconds(15);
        Program.Check(
            aggregate.FailureTiming!.LatestStarted!.StartedAt < boundary && aggregate.FailureTiming.LatestCompleted!.CompletedAt > boundary,
            "An earlier long-running failure crossing verification cannot be mistaken for entirely earlier activity"
        );
        Program.Check(
            first.Failures == 1
                && first.FailureTiming!.LatestCompleted!.CompletedAt == Start.AddSeconds(11)
                && first.LastFailure == 0xc0000022,
            "A drained aggregate retains immutable timing evidence when collection continues"
        );
        buffer.Add(Operation(10, 12, "Write", 0xc0000001));
        aggregate = buffer.Drain().Evidence.Files.Single();
        Program.Check(
            aggregate.LastFailedOperation == "Write"
                && aggregate.LastFailure == 0xc0000001
                && aggregate.LastFailureDescription == WindowsFileStatus.Describe(0xc0000001)
                && aggregate.FailureTiming!.LatestStarted!.CompletedAt == Start.AddSeconds(12),
            "Equal-start failure witnesses prefer later completion and keep legacy error fields attached to that operation"
        );
        buffer.Add(Operation(30, 31, "Delete", 0xc0000022) with { CompletedAt = null });
        aggregate = buffer.Drain().Evidence.Files.Single();
        Program.Check(
            aggregate.Failures == 4
                && aggregate.FailureTiming!.UntimedCount == 1
                && aggregate.LastFailure == 0xc0000001
                && aggregate.LastFailedOperation == "Write",
            "Untimed failures remain counted without replacing a known timed error with arrival-order metadata"
        );
    }

    private static void ModificationOrdering()
    {
        var buffer = new TraceBuffer();
        buffer.Add(Operation(10, 11, "Rename"));
        buffer.Add(Operation(1, 20, "Write"));
        buffer.Add(Operation(100, 101, "Read"));
        var aggregate = buffer.Drain().Evidence.Files.Single();
        Program.Check(
            aggregate.Modifications == 2
                && aggregate.ModificationTiming!.LatestStarted is { Operation: "Rename", Status: 0 }
                && aggregate.ModificationTiming.LatestStarted.StartedAt == Start.AddSeconds(10)
                && aggregate.ModificationTiming.LatestCompleted is { Operation: "Write", Status: 0 }
                && aggregate.ModificationTiming.LatestCompleted.CompletedAt == Start.AddSeconds(20)
                && aggregate.LastAt == Start.AddSeconds(101),
            "Modification timing follows its own operations and cannot inherit a later unrelated read timestamp"
        );
        buffer.Add(Operation(30, 29, "Delete"));
        aggregate = buffer.Drain().Evidence.Files.Single();
        Program.Check(
            aggregate.Modifications == 3
                && aggregate.ModificationTiming!.UntimedCount == 1
                && aggregate.ModificationTiming.LatestCompleted!.CompletedAt == Start.AddSeconds(20),
            "Invalid modification completion timing remains explicitly unknown"
        );
    }

    private static void TiesAndMissingTiming()
    {
        var candidates = new[]
        {
            Operation(10, 20, "Read", 0xc0000022),
            Operation(10, 21, "Read", 0xc0000022),
            Operation(11, 21, "Read", 0xc0000022),
            Operation(11, 21, "Write", 0xc0000022),
            Operation(11, 21, "Write", 0xc0000034),
        };
        var forward = candidates.Aggregate(new TraceActivityTiming(), (timing, operation) => timing.Add(operation));
        var reverse = candidates.Reverse().Aggregate(new TraceActivityTiming(), (timing, operation) => timing.Add(operation));
        Program.Check(
            forward == reverse
                && forward.LatestStarted is { Operation: "Write", Status: 0xc0000034 }
                && forward.LatestStarted.StartedAt == Start.AddSeconds(11)
                && forward.LatestCompleted == forward.LatestStarted,
            "Start, completion, operation and status ties are deterministic across reversed delivery order"
        );
        var invalid = Operation(30, 31, "Read", 0xc0000022);
        var missing = forward
            .Add(invalid with { CompletedAt = null })
            .Add(invalid with { Timestamp = default })
            .Add(invalid with { CompletedAt = default(DateTimeOffset) })
            .Add(invalid with { CompletedAt = Start })
            .Add(invalid with { Status = null })
            .Add(invalid with { Status = 0x103 });
        Program.Check(
            missing.UntimedCount == 6
                && missing.LatestStarted == forward.LatestStarted
                && missing.LatestCompleted == forward.LatestCompleted,
            "Missing, reversed and incomplete timing increments unknown activity without inventing witnesses"
        );
    }

    private static void OverflowAndSerialization()
    {
        var buffer = new TraceBuffer();
        for (var index = 0; index < 4200; index++)
        {
            buffer.Add(Operation(index, index + 1, "Read", 0xc0000022));
            buffer.Add(Operation(index + 5000, index + 5001, "Write"));
            buffer.Add(Operation(index + 10000, index + 10001, "Read", 0xc01c0004));
        }

        var batch = buffer.Drain();
        var aggregate = batch.Evidence.Files.Single();
        Program.Check(
            batch.Evidence.QueueDropped > 0
                && aggregate.Failures == 4200
                && aggregate.Modifications == 4200
                && aggregate.FastIoFallbacks == 4200
                && aggregate.FailureTiming!.LatestStarted!.StartedAt == Start.AddSeconds(4199)
                && aggregate.ModificationTiming!.LatestCompleted!.CompletedAt == Start.AddSeconds(9200),
            "Bounded failure, modification and fallback evidence survives raw transport overflow"
        );
        var clone = JsonSerializer.Deserialize<TraceBatch>(JsonSerializer.Serialize(batch))!;
        var cloned = clone.Evidence.Files.Single();
        Program.Check(
            cloned.FailureTiming == aggregate.FailureTiming
                && cloned.ModificationTiming == aggregate.ModificationTiming
                && cloned.FastIoFallbacks == 4200
                && cloned.LastFailure == aggregate.LastFailure,
            "Shared-contract JSON round-trip preserves activity witnesses and new failure-count semantics"
        );
        var legacy = JsonSerializer.Deserialize<TraceAggregate>("{\"Failures\":3,\"Modifications\":2}")!;
        Program.Check(
            legacy.Failures == 3
                && legacy.Modifications == 2
                && legacy.FastIoFallbacks is null
                && legacy.FailureTiming is null
                && legacy.ModificationTiming is null,
            "Legacy aggregates keep their totals with unknown failure classification and timing"
        );
    }

    private static void MaximumBatchSize()
    {
        var buffer = new TraceBuffer();
        for (var index = 0; index < 512; index++)
        {
            var path = Path.Combine(@"C:\cache", new string('a', 180), $"LockScreen_{index:D3}", "LockScreen__1920_1080_notdimmed.jpg");
            TraceOperation At(int started, int completed, string operation, uint status = 0) =>
                Operation(started, completed, operation, status) with
                {
                    Path = path,
                    ProcessName = "SyntheticReaderWithTypicalName.exe",
                };
            buffer.Add(At(10, 11, "Read", 0xc0000022));
            buffer.Add(At(1, 20, "Open", 0xc0000034));
            buffer.Add(At(30, 31, "Rename"));
            buffer.Add(At(21, 40, "Write"));
            buffer.Add(At(41, 42, "Read", 0xc01c0004));
        }

        var batch = buffer.Drain();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new HelperReply(PipeProtocol.Version, 1, Batch: batch));
        Program.Check(
            batch.Evidence.Files.Count == 512
                && batch.Operations.Count == 128
                && batch.Evidence.OmittedAggregates == 0
                && bytes.Length < PipeProtocol.MaximumMessageBytes,
            $"Full aggregate and operation batch with four witnesses per typical cache path fits IPC limit ({bytes.Length:N0} bytes)"
        );
    }
}
