namespace LockscreenGif.Privileged;

public sealed record TraceOperation(
    DateTimeOffset Timestamp,
    string Path,
    string Operation,
    int ProcessId,
    long ProcessInstance,
    string ProcessName,
    int? SessionId,
    bool IsApp,
    bool AttributionResolved,
    long? RequestedBytes,
    long? CompletedBytes,
    uint? Status,
    string? NewPath = null,
    DateTimeOffset? CompletedAt = null
)
{
    public bool Succeeded => Status is uint status && (status & 0x80000000) == 0 && status != 0x103;

    // Reading until EOF is normal stream termination, not a failed image access.
    // Keep its original NTSTATUS in the operation; it is not a successful data read.
    public bool IsEndOfFile => Operation == "Read" && Status == 0xc0000011 && CompletedBytes is null or 0;

    // The fast-I/O attempt falls back to another I/O path; preserve it without calling it terminal failure or success.
    public bool IsFastIoFallback => Status == 0xc01c0004;
    public bool IsFailure => Status is uint status && (status & 0x80000000) != 0 && !IsEndOfFile && !IsFastIoFallback;
}

public sealed class TraceAggregate
{
    public string Path { get; set; } = "";
    public int ProcessId { get; set; }
    public long ProcessInstance { get; set; }
    public string ProcessName { get; set; } = "";
    public int? SessionId { get; set; }
    public bool IsApp { get; set; }
    public bool AttributionResolved { get; set; }
    public long Reads { get; set; }
    public long ReadBytes { get; set; }

    // Whole-collection totals above include old contents and inspection activity.
    // These timestamps describe one successful read even if its raw record was omitted.
    public DateTimeOffset? LastReadStartedAt { get; set; }
    public DateTimeOffset? LastReadCompletedAt { get; set; }
    public long Modifications { get; set; }
    public long Failures { get; set; }

    // Null identifies legacy aggregates whose failure totals can include fast-I/O fallback.
    public long? FastIoFallbacks { get; set; }
    public TraceActivityTiming? FailureTiming { get; set; }
    public TraceActivityTiming? ModificationTiming { get; set; }
    public uint? LastFailure { get; set; }
    public string? LastFailedOperation { get; set; }
    public string? LastFailureDescription { get; set; }
    public DateTimeOffset FirstAt { get; set; }
    public DateTimeOffset LastAt { get; set; }
}

public sealed class ProcessTraceEvidence
{
    public string State { get; set; } = "NotStarted";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? Reason { get; set; }
    public long EventsLost { get; set; }
    public long QueueDropped { get; set; }
    public long UnmatchedOperations { get; set; }
    public long UnmatchedCompletions { get; set; }
    public long UnresolvedPaths { get; set; }
    public long UnresolvedProcesses { get; set; }
    public long OmittedOperations { get; set; }
    public long OmittedAggregates { get; set; }
    public TraceShutdownEvidence? Shutdown { get; set; }
    public List<TraceOperation> Operations { get; set; } = [];
    public List<TraceAggregate> Files { get; set; } = [];

    // Unscoped system events cannot establish a missing operation on a relevant image.
    // Keep those uncertainties in exports without marking a finished collector as failed.
    public bool HasUnresolvedActivity => UnresolvedPaths > 0 || UnmatchedCompletions > 0;
    public bool HasCollectionGaps =>
        EventsLost > 0
        || QueueDropped > 0
        || UnmatchedOperations > 0
        || UnresolvedProcesses > 0
        || OmittedOperations > 0
        || OmittedAggregates > 0;
    public bool HasGaps => HasCollectionGaps || HasUnresolvedActivity;
}

public sealed record TraceScope(string CacheDirectory, string SourcePath);

public sealed record TraceBatch(ProcessTraceEvidence Evidence, List<TraceOperation> Operations, bool HasMore);
