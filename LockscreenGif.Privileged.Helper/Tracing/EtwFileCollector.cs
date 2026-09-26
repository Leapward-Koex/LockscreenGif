using System.Diagnostics;
using LockscreenGif.Privileged;
using Microsoft.Diagnostics.Tracing;

namespace LockscreenGif.Privileged.Helper.Tracing;

internal sealed partial class EtwFileCollector
{
    private readonly object _gate = new();
    private readonly TraceBuffer _buffer = new();
    private readonly TraceProgressRecorder _progress = new();
    private NativeTraceSession? _native;
    private ETWTraceEventSource? _source;
    private Task? _worker;
    private Task? _stopping;
    private bool _stopRequested;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(TraceScope scope, int appPid)
    {
        if (_worker is not null)
        {
            throw new InvalidOperationException("Tracing has already been started.");
        }

        _worker = Task.Factory.StartNew(
            () => Run(scope, appPid),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
        return _started.Task;
    }

    private void Run(TraceScope scope, int appPid)
    {
        FileOperationCorrelator? correlator = null;
        try
        {
            using var native = new NativeTraceSession();
            using var source = new ETWTraceEventSource(NativeTraceSession.Name, TraceEventSourceType.Session);
            try
            {
                var identities = new TraceIdentityMap(appPid, Environment.ProcessId, () => _buffer.Evidence.UnresolvedProcesses++);
                correlator = new(new(scope), identities, _buffer);
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        try
                        {
                            identities.ProcessStart(
                                process.Id,
                                0,
                                process.ProcessName,
                                process.SessionId,
                                process.StartTime.ToUniversalTime()
                            );
                        }
                        catch (Exception)
                        { /* Unavailable identity remains unknown. */
                        }
                    }
                }
                EtwFileSubscriptions.Attach(source, identities, correlator, _gate, _progress);
                lock (_gate)
                {
                    _native = native;
                    _source = source;
                    if (_stopRequested)
                    {
                        throw new OperationCanceledException("Tracing was cancelled before startup.");
                    }

                    native.Enable((ulong)EtwFileSubscriptions.Keywords);
                    _buffer.Evidence.StartedAt = DateTimeOffset.UtcNow;
                    _buffer.Evidence.State = "Recording";
                }
                _started.TrySetResult();
                _progress.ConsumerStarting();
                try
                {
                    _progress.ConsumerReturned(source.Process());
                }
                catch (Exception ex)
                {
                    _progress.ConsumerReturned(null, ex.GetType().Name);
                    throw;
                }
            }
            finally
            {
                _progress.CleanupStarting();
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _buffer.Evidence.State = _buffer.Evidence.StartedAt is null ? "Unavailable" : "Incomplete";
                _buffer.Evidence.Reason =
                    $"Trace collection failed: {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message[..Math.Min(ex.Message.Length, 512)]}";
            }
            _started.TrySetException(ex);
        }
        finally
        {
            _progress.CorrelationStarting();
            lock (_gate)
            {
                correlator?.Finish();
                _native = null;
                _source = null;
                _buffer.Evidence.EndedAt = DateTimeOffset.UtcNow;
                if (_buffer.Evidence.State == "Recording")
                {
                    _buffer.Evidence.State = _stopRequested && !_buffer.Evidence.HasCollectionGaps ? "Completed" : "Incomplete";
                }

                if (_buffer.Evidence.State == "Incomplete" && _buffer.Evidence.Reason is null)
                {
                    _buffer.Evidence.Reason =
                        "Some operations could not be fully resolved or collection lost evidence. See trace coverage counters in the report.";
                }

                _progress.CleanupCompleted();
            }
        }
    }

    public TraceBatch Drain()
    {
        lock (_gate)
        {
            var batch = _buffer.Drain();
            batch.Evidence.Shutdown = _progress.Snapshot();
            return batch;
        }
    }
}
