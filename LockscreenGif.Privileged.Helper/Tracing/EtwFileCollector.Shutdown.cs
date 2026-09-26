using System.Diagnostics;

namespace LockscreenGif.Privileged.Helper.Tracing;

internal sealed partial class EtwFileCollector
{
    public Task StopAsync(string? reason = null)
    {
        lock (_gate)
        {
            return _stopping ??= StopCoreAsync(reason);
        }
    }

    private async Task StopCoreAsync(string? reason)
    {
        await Task.Yield();
        _progress.StopRequested();
        NativeTraceSession? native;
        lock (_gate)
        {
            _stopRequested = true;
            if (reason is not null)
            {
                MarkIncomplete(reason);
            }

            native = _native;
        }
        if (native is not null)
        {
            _progress.NativeStopStarting();
            var started = Stopwatch.GetTimestamp();
            NativeTraceStopResult? result = null;
            try
            {
                result = native.Stop();
                result.EnsureSuccess();
                _progress.NativeStopReturned(result, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                lock (_gate)
                {
                    _buffer.Evidence.EventsLost += result.EventsLost;
                }
            }
            catch (Exception ex)
            {
                _progress.NativeStopReturned(result, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.GetType().Name);
                MarkIncomplete($"File activity monitoring could not stop normally: {ex.GetType().Name}. Some final events may be missing.");
                _progress.ForceStopRequested();
                ForceStop();
            }
        }
        if (_worker is not null)
        {
            await TraceWorkerDrain.WaitAsync(_worker, _progress, ForceStop, MarkIncomplete);
        }
    }

    private void MarkIncomplete(string reason)
    {
        lock (_gate)
        {
            _buffer.Evidence.State = "Incomplete";
            _buffer.Evidence.Reason ??= reason;
        }
    }

    private void ForceStop()
    {
        lock (_gate)
        {
            _source?.StopProcessing();
        }
    }
}
