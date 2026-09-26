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
    public bool Disposed { get; private set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public TimeSpan StopDelay { get; set; }
    public System.Collections.Concurrent.ConcurrentQueue<TraceBatch> Batches { get; } = new();
    private bool _stopped;

    public Task StartTraceAsync(TraceScope scope, CancellationToken token)
    {
        Starts++;
        _stopped = false;
        if (Decline)
        {
            throw new OperationCanceledException("Synthetic UAC decline");
        }

        return Task.CompletedTask;
    }

    public Task<TraceBatch> ReadTraceAsync(CancellationToken token)
    {
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
                new()
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
        await Task.Delay(StopDelay, token);
        _stopped = true;
    }

    public Task<int> GrantAsync(string path, bool write, CancellationToken token) => Task.FromResult(Decline || Disconnect ? 5 : 0);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
