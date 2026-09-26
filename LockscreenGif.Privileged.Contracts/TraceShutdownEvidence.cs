namespace LockscreenGif.Privileged;

/// <summary>Bounded shutdown measurements. Times describe the collector, not GIF playback.</summary>
public sealed record TraceShutdownEvidence
{
    public DateTimeOffset? ConsumerStartedAt { get; init; }
    public DateTimeOffset? StopRequestedAt { get; init; }
    public DateTimeOffset? NativeStopStartedAt { get; init; }
    public DateTimeOffset? NativeStopReturnedAt { get; init; }
    public double? NativeStopElapsedMilliseconds { get; init; }
    public bool? NativeStopAttempted { get; init; }
    public uint? NativeStopStatus { get; init; }
    public string? NativeStopFailureType { get; init; }
    public TraceSessionBufferStatistics? NativeStopBuffers { get; init; }
    public DateTimeOffset? DrainWaitStartedAt { get; init; }
    public DateTimeOffset? DrainDeadlineExceededAt { get; init; }
    public double? DrainWaitElapsedMilliseconds { get; init; }
    public string? StageAtDeadline { get; init; }
    public DateTimeOffset? ForceStopRequestedAt { get; init; }
    public DateTimeOffset? ForcedStopGraceExceededAt { get; init; }
    public DateTimeOffset? ConsumerReturnedAt { get; init; }
    public bool? ConsumerCompletedNormally { get; init; }
    public string? ConsumerFailureType { get; init; }
    public DateTimeOffset? CleanupStartedAt { get; init; }
    public DateTimeOffset? CleanupCompletedAt { get; init; }
    public string WorkerStage { get; init; } = "Preparing";
    public TraceConsumerProgress Current { get; init; } = new();
    public TraceConsumerProgress? AtStopRequest { get; init; }
    public TraceConsumerProgress? AtNativeStopReturn { get; init; }
    public TraceConsumerProgress? AtDrainDeadline { get; init; }
}

/// <summary>Counts all dispatched ETW events, including unrelated events; no event payloads are kept.</summary>
public sealed record TraceConsumerProgress
{
    public long CallbacksStarted { get; init; }
    public long CallbacksFinished { get; init; }
    public DateTimeOffset? LatestEventTimestamp { get; init; }
    public DateTimeOffset? LastCallbackStartedAt { get; init; }
    public DateTimeOffset? LastCallbackFinishedAt { get; init; }
    public bool CallbackInProgress => CallbacksStarted > CallbacksFinished;
}

/// <summary>Native session statistics returned by ControlTrace STOP, not consumer buffer counts.</summary>
public sealed record TraceSessionBufferStatistics(
    uint BufferCount,
    uint FreeBuffers,
    uint BuffersWritten,
    uint EventsLost,
    uint RealTimeBuffersLost
);
