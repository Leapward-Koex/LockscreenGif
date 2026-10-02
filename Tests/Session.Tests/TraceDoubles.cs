using LockscreenGif.Privileged;

namespace LockscreenGif.Services.Diagnostics;

public sealed class PrivilegedSessionFactory(Func<string, IPrivilegedOperationSession> create)
{
    public IPrivilegedOperationSession Create(string root) => create(root);
}

public sealed class FakePrivilegedSession : IPrivilegedOperationSession
{
    public bool Decline { get; set; }
    public bool Disconnect { get; set; }
    public Exception? StartError { get; set; }
    public Exception? ReadError { get; set; }
    public Exception? StopError { get; set; }
    public Exception? DisposeError { get; set; }
    public bool Disposed { get; private set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public TimeSpan StopDelay { get; set; }
    public TaskCompletionSource? StartGate { get; set; }
    public Func<bool, ProcessTraceEvidence>? EvidenceFactory { get; set; }
    public System.Collections.Concurrent.ConcurrentQueue<TraceBatch> Batches { get; } = new();
    private bool _stopped;

    public async Task StartTraceAsync(TraceScope scope, CancellationToken token)
    {
        Starts++;
        _stopped = false;
        if (StartError is { } startError)
        {
            throw startError;
        }
        if (Decline)
        {
            throw new OperationCanceledException("Synthetic UAC decline");
        }

        if (StartGate is not null)
        {
            await StartGate.Task.WaitAsync(token);
        }
    }

    public Task<TraceBatch> ReadTraceAsync(CancellationToken token)
    {
        if (ReadError is { } readError)
        {
            throw readError;
        }
        if (Disconnect)
        {
            throw new IOException("Synthetic disconnect");
        }

        if (Batches.TryDequeue(out var batch))
        {
            return Task.FromResult(batch);
        }

        return Task.FromResult(
            new TraceBatch(
                EvidenceFactory?.Invoke(_stopped)
                    ?? new()
                    {
                        State = _stopped ? "Completed" : "Recording",
                        StartedAt = DateTimeOffset.UtcNow,
                        EndedAt = _stopped ? DateTimeOffset.UtcNow : null,
                    },
                [],
                false
            )
        );
    }

    public async Task StopTraceAsync(CancellationToken token)
    {
        Stops++;
        if (StopError is { } stopError)
        {
            throw stopError;
        }
        await Task.Delay(StopDelay, token);
        _stopped = true;
    }

    public Task<int> GrantAsync(string path, bool write, CancellationToken token) => Task.FromResult(Decline || Disconnect ? 5 : 0);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        if (DisposeError is { } disposeError)
        {
            throw disposeError;
        }
        return ValueTask.CompletedTask;
    }
}
