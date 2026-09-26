using LockscreenGif.Privileged;

namespace LockscreenGif.Services.Diagnostics;

/// <summary>Feeds bounded trace evidence to the recorder without blocking ETW callbacks or the UI.</summary>
internal sealed class DiagnosticProcessTrace(
    IPrivilegedOperationSession helper,
    DiagnosticRecorder recorder,
    Func<CancellationToken, Task>? waitForPoll = null
)
{
    private readonly CancellationTokenSource _pollStop = new();
    private Task? _poll;
    private bool _started;
    private readonly object _finishGate = new();
    private Task? _finish;
    private long _retainedBytes;
    private readonly List<TraceOperation> _operations = [];
    private long _omitted;
    private string? _lastState,
        _lastReason;

    public async Task StartAsync(TraceScope scope, CancellationToken token)
    {
        recorder.Update(s => s.ProcessTrace.State = "Starting");
        try
        {
            await helper.StartTraceAsync(scope, token);
            _started = true;
            var first = await helper.ReadTraceAsync(token);
            Merge(first);
            _poll = Task.Run(() => PollAsync(first.HasMore));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Unavailable($"Process tracing could not start: {ex.Message} Basic checks continue.");
        }
    }

    private async Task PollAsync(bool hasMore)
    {
        try
        {
            while (true)
            {
                _pollStop.Token.ThrowIfCancellationRequested();
                if (hasMore)
                {
                    await Task.Yield();
                }
                else
                {
                    await (waitForPoll?.Invoke(_pollStop.Token) ?? Task.Delay(200, _pollStop.Token));
                }

                var batch = await helper.ReadTraceAsync(_pollStop.Token);
                Merge(batch);
                hasMore = batch.HasMore;
            }
        }
        catch (OperationCanceledException) when (_pollStop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Unavailable($"Process tracing disconnected: {ex.Message}");
        }
    }

    public Task FinishAsync()
    {
        lock (_finishGate)
        {
            return _finish ??= FinishCoreAsync();
        }
    }

    private async Task FinishCoreAsync()
    {
        await Task.Yield();
        _pollStop.Cancel();
        if (_poll is not null)
        {
            await _poll;
        }

        if (!_started)
        {
            _pollStop.Dispose();
            return;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await helper.StopTraceAsync(timeout.Token);
            TraceBatch batch;
            do
            {
                batch = await helper.ReadTraceAsync(timeout.Token);
                Merge(batch);
            } while (batch.HasMore);
        }
        catch (Exception ex)
        {
            Unavailable($"Final trace collection failed: {ex.GetType().Name}.");
        }
        finally
        {
            _pollStop.Dispose();
        }
    }

    private void Merge(TraceBatch batch)
    {
        foreach (var operation in batch.Operations)
        {
            if (_operations.Count >= 10000)
            {
                _omitted++;
                continue;
            }
            var size = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(operation).Length;
            if (_retainedBytes + size > 16 * 1024 * 1024)
            {
                _omitted++;
                continue;
            }
            _operations.Add(operation);
            _retainedBytes += size;
        }
        batch.Evidence.OmittedOperations = _omitted;
        if (batch.Evidence.State == "Completed" && batch.Evidence.HasCollectionGaps)
        {
            batch.Evidence.State = "Incomplete";
        }
        // Copy the list: collector snapshots never share mutable evidence with future updates.
        batch.Evidence.Operations = _operations.ToList();
        var notify = _lastState != batch.Evidence.State || _lastReason != batch.Evidence.Reason;
        _lastState = batch.Evidence.State;
        _lastReason = batch.Evidence.Reason;
        recorder.Update(s => s.ProcessTrace = batch.Evidence, notify);
    }

    private void Unavailable(string reason) =>
        recorder.Update(s =>
        {
            s.ProcessTrace.State = s.ProcessTrace.StartedAt is null ? "Unavailable" : "Incomplete";
            s.ProcessTrace.Reason = reason;
        });
}
