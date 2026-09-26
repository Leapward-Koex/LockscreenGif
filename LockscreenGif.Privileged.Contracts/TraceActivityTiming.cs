namespace LockscreenGif.Privileged;

public sealed record TraceActivityWitness(DateTimeOffset StartedAt, DateTimeOffset CompletedAt, string Operation, uint Status);

/// <summary>Bounded event-time evidence; counts elsewhere still span the whole collection.</summary>
public sealed record TraceActivityTiming(
    TraceActivityWitness? LatestStarted = null,
    TraceActivityWitness? LatestCompleted = null,
    long UntimedCount = 0
)
{
    public TraceActivityTiming Add(TraceOperation operation)
    {
        if (
            operation.Timestamp == default
            || operation.CompletedAt is not { } completed
            || completed == default
            || completed < operation.Timestamp
            || operation.Status is not { } status
            || status == 0x103
        )
        {
            return this with { UntimedCount = UntimedCount + 1 };
        }

        var witness = new TraceActivityWitness(operation.Timestamp, completed, operation.Operation, status);
        return this with
        {
            LatestStarted = LatestStarted is null || Compare(witness, LatestStarted, byCompletion: false) > 0 ? witness : LatestStarted,
            LatestCompleted =
                LatestCompleted is null || Compare(witness, LatestCompleted, byCompletion: true) > 0 ? witness : LatestCompleted,
        };
    }

    private static int Compare(TraceActivityWitness left, TraceActivityWitness right, bool byCompletion)
    {
        var primary = byCompletion ? left.CompletedAt.CompareTo(right.CompletedAt) : left.StartedAt.CompareTo(right.StartedAt);
        if (primary != 0)
        {
            return primary;
        }

        var secondary = byCompletion ? left.StartedAt.CompareTo(right.StartedAt) : left.CompletedAt.CompareTo(right.CompletedAt);
        if (secondary != 0)
        {
            return secondary;
        }

        // Stable metadata tie-breaks keep output independent of event delivery order.
        var operation = StringComparer.Ordinal.Compare(left.Operation, right.Operation);
        return operation != 0 ? operation : left.Status.CompareTo(right.Status);
    }
}
