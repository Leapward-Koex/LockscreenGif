using LockscreenGif.Privileged;

namespace LockscreenGif.Privileged.Helper.Tracing;

/// <summary>Separate from the correlation lock so a blocked dispatch remains observable.</summary>
internal sealed class TraceProgressRecorder
{
    private readonly object _gate = new();
    private TraceShutdownEvidence _shutdown = new();
    private long _started,
        _finished;
    private DateTimeOffset? _latestEvent,
        _lastStarted,
        _lastFinished;

    public void DispatchStarted(DateTimeOffset eventTimestamp)
    {
        lock (_gate)
        {
            _started++;
            _lastStarted = DateTimeOffset.UtcNow;
            if (_latestEvent is null || eventTimestamp > _latestEvent)
            {
                _latestEvent = eventTimestamp;
            }
        }
    }

    public void DispatchFinished()
    {
        lock (_gate)
        {
            _finished++;
            _lastFinished = DateTimeOffset.UtcNow;
        }
    }

    private TraceConsumerProgress Capture() =>
        new()
        {
            CallbacksStarted = _started,
            CallbacksFinished = _finished,
            LatestEventTimestamp = _latestEvent,
            LastCallbackStartedAt = _lastStarted,
            LastCallbackFinishedAt = _lastFinished,
        };

    public TraceShutdownEvidence Snapshot()
    {
        lock (_gate)
        {
            return _shutdown with { Current = Capture() };
        }
    }

    public void ConsumerStarting()
    {
        lock (_gate)
        {
            _shutdown = _shutdown with { ConsumerStartedAt = DateTimeOffset.UtcNow, WorkerStage = "Processing" };
        }
    }

    public void ConsumerReturned(bool? completedNormally, string? failureType = null)
    {
        lock (_gate)
        {
            _shutdown = _shutdown with
            {
                ConsumerReturnedAt = DateTimeOffset.UtcNow,
                ConsumerCompletedNormally = completedNormally,
                ConsumerFailureType = failureType,
                WorkerStage = "Consumer returned",
            };
        }
    }

    public void StopRequested()
    {
        lock (_gate)
        {
            if (_shutdown.StopRequestedAt is null)
            {
                _shutdown = _shutdown with { StopRequestedAt = DateTimeOffset.UtcNow, AtStopRequest = Capture() };
            }
        }
    }

    public void NativeStopStarting()
    {
        lock (_gate)
        {
            _shutdown = _shutdown with { NativeStopStartedAt = DateTimeOffset.UtcNow };
        }
    }

    public void NativeStopReturned(NativeTraceStopResult? result, double elapsedMilliseconds, string? failureType = null)
    {
        lock (_gate)
        {
            _shutdown = _shutdown with
            {
                NativeStopReturnedAt = DateTimeOffset.UtcNow,
                NativeStopElapsedMilliseconds = elapsedMilliseconds,
                NativeStopAttempted = result?.Attempted,
                NativeStopStatus = result?.Status,
                NativeStopBuffers = result?.Buffers,
                NativeStopFailureType = failureType,
                AtNativeStopReturn = Capture(),
            };
        }
    }

    public void DrainStarting()
    {
        lock (_gate)
        {
            _shutdown = _shutdown with { DrainWaitStartedAt = DateTimeOffset.UtcNow };
        }
    }

    public void DrainTimedOut(double elapsedMilliseconds)
    {
        lock (_gate)
        {
            _shutdown = _shutdown with
            {
                DrainDeadlineExceededAt = DateTimeOffset.UtcNow,
                DrainWaitElapsedMilliseconds = elapsedMilliseconds,
                StageAtDeadline = _shutdown.WorkerStage,
                AtDrainDeadline = Capture(),
            };
        }
    }

    public void ForceStopRequested()
    {
        lock (_gate)
        {
            if (_shutdown.ForceStopRequestedAt is null)
            {
                _shutdown = _shutdown with { ForceStopRequestedAt = DateTimeOffset.UtcNow };
            }
        }
    }

    public void GraceExceeded()
    {
        lock (_gate)
        {
            _shutdown = _shutdown with { ForcedStopGraceExceededAt = DateTimeOffset.UtcNow };
        }
    }

    public void CleanupStarting()
    {
        lock (_gate)
        {
            _shutdown = _shutdown with { CleanupStartedAt = DateTimeOffset.UtcNow, WorkerStage = "Disposing" };
        }
    }

    public void CorrelationStarting()
    {
        lock (_gate)
        {
            _shutdown = _shutdown with { WorkerStage = "Final correlation" };
        }
    }

    public void CleanupCompleted()
    {
        lock (_gate)
        {
            _shutdown = _shutdown with { CleanupCompletedAt = DateTimeOffset.UtcNow, WorkerStage = "Finished" };
        }
    }
}
