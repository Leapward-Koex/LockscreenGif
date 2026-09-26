using LockscreenGif.Privileged;

namespace LockscreenGif.Privileged.Helper.Tracing;

// All callers serialize access using the collector gate. No disk-backed logging.
internal sealed class TraceBuffer
{
    private readonly Queue<TraceOperation> _queue = new();
    private readonly Dictionary<(string, int, long), TraceAggregate> _aggregates = new();
    public ProcessTraceEvidence Evidence { get; } = new();

    public void Add(TraceOperation item)
    {
        var key = (item.Path.ToUpperInvariant(), item.ProcessId, item.ProcessInstance);
        if (!_aggregates.TryGetValue(key, out var aggregate))
        {
            if (_aggregates.Count >= 512)
            {
                Evidence.OmittedAggregates++;
            }
            else
            {
                aggregate = new()
                {
                    Path = item.Path,
                    ProcessId = item.ProcessId,
                    ProcessInstance = item.ProcessInstance,
                    ProcessName = item.ProcessName,
                    SessionId = item.SessionId,
                    IsApp = item.IsApp,
                    AttributionResolved = item.AttributionResolved,
                    FirstAt = item.Timestamp,
                    FastIoFallbacks = 0,
                    FailureTiming = new(),
                    ModificationTiming = new(),
                };
                _aggregates.Add(key, aggregate);
            }
        }
        if (aggregate is not null)
        {
            var completed = item.CompletedAt ?? item.Timestamp;
            if (completed > aggregate.LastAt)
            {
                aggregate.LastAt = completed;
            }

            if (item.Timestamp < aggregate.FirstAt)
            {
                aggregate.FirstAt = item.Timestamp;
            }

            if (item.Succeeded && item.Operation == "Read" && item.CompletedBytes > 0)
            {
                aggregate.Reads++;
                aggregate.ReadBytes += item.CompletedBytes.Value;
                if (
                    item.CompletedAt is { } ended
                    && ended >= item.Timestamp
                    && (aggregate.LastReadStartedAt is null || item.Timestamp > aggregate.LastReadStartedAt)
                )
                {
                    aggregate.LastReadStartedAt = item.Timestamp;
                    aggregate.LastReadCompletedAt = ended;
                }
            }
            if (item.Succeeded && item.Operation is "Write" or "Rename" or "Delete")
            {
                aggregate.Modifications++;
                aggregate.ModificationTiming = aggregate.ModificationTiming!.Add(item);
            }

            if (item.IsFastIoFallback)
            {
                aggregate.FastIoFallbacks++;
            }

            if (item.IsFailure)
            {
                aggregate.Failures++;
                var previous = aggregate.FailureTiming!.LatestStarted;
                aggregate.FailureTiming = aggregate.FailureTiming.Add(item);
                if (aggregate.FailureTiming.LatestStarted is { } latest && latest != previous)
                {
                    aggregate.LastFailure = latest.Status;
                    aggregate.LastFailedOperation = latest.Operation;
                    aggregate.LastFailureDescription = WindowsFileStatus.Describe(latest.Status);
                }
            }
        }
        if (!item.AttributionResolved)
        {
            Evidence.UnresolvedProcesses++;
        }

        if (_queue.Count == 4096)
        {
            Evidence.QueueDropped++;
        }
        else
        {
            _queue.Enqueue(item);
        }
    }

    public TraceBatch Drain()
    {
        var operations = new List<TraceOperation>();
        while (operations.Count < 128 && _queue.TryDequeue(out var item))
        {
            operations.Add(item);
        }
        // Snapshot while locked; IPC serialization runs after releasing the gate.
        var stats = new ProcessTraceEvidence
        {
            State = Evidence.State,
            StartedAt = Evidence.StartedAt,
            EndedAt = Evidence.EndedAt,
            Reason = Evidence.Reason,
            EventsLost = Evidence.EventsLost,
            QueueDropped = Evidence.QueueDropped,
            UnmatchedOperations = Evidence.UnmatchedOperations,
            UnmatchedCompletions = Evidence.UnmatchedCompletions,
            UnresolvedPaths = Evidence.UnresolvedPaths,
            UnresolvedProcesses = Evidence.UnresolvedProcesses,
            OmittedAggregates = Evidence.OmittedAggregates,
            Files = _aggregates
                .Values.Select(a => new TraceAggregate
                {
                    Path = a.Path,
                    ProcessId = a.ProcessId,
                    ProcessInstance = a.ProcessInstance,
                    ProcessName = a.ProcessName,
                    SessionId = a.SessionId,
                    IsApp = a.IsApp,
                    AttributionResolved = a.AttributionResolved,
                    Reads = a.Reads,
                    ReadBytes = a.ReadBytes,
                    Modifications = a.Modifications,
                    Failures = a.Failures,
                    FastIoFallbacks = a.FastIoFallbacks,
                    FailureTiming = a.FailureTiming,
                    ModificationTiming = a.ModificationTiming,
                    LastReadStartedAt = a.LastReadStartedAt,
                    LastReadCompletedAt = a.LastReadCompletedAt,
                    LastFailure = a.LastFailure,
                    LastFailedOperation = a.LastFailedOperation,
                    LastFailureDescription = a.LastFailureDescription,
                    FirstAt = a.FirstAt,
                    LastAt = a.LastAt,
                })
                .ToList(),
        };
        return new(stats, operations, _queue.Count > 0);
    }
}
