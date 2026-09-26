using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Diagnostics;

namespace Session.Tests;

internal static class TracePollingTests
{
    public static async Task RunAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var helper = new FakePrivilegedSession();
        var recorder = new DiagnosticRecorder(new DiagnosticSession());
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleCalls = 0;
        async Task WaitForPoll(CancellationToken token)
        {
            if (Interlocked.Increment(ref idleCalls) == 1)
            {
                idle.TrySetResult();
                await resume.Task.WaitAsync(token);
            }
            else
            {
                await Task.Delay(Timeout.Infinite, token);
            }
        }
        TraceBatch Batch(int index, bool more) =>
            new(
                new() { State = "Recording", StartedAt = now },
                [
                    new(
                        now.AddMilliseconds(index),
                        @"C:\cache\LockScreen.jpg",
                        "Read",
                        77,
                        1,
                        "Reader.exe",
                        1,
                        false,
                        true,
                        1,
                        1,
                        0,
                        CompletedAt: now.AddMilliseconds(index + 1)
                    ),
                ],
                more
            );
        helper.Batches.Enqueue(Batch(0, true));
        helper.Batches.Enqueue(Batch(1, true));
        helper.Batches.Enqueue(Batch(2, false));
        var trace = new DiagnosticProcessTrace(helper, recorder, WaitForPoll);
        await trace.StartAsync(new(@"C:\cache", @"C:\source.gif"), default);
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Program.Check(
            recorder.Snapshot().ProcessTrace.Operations.Count == 3,
            "Initial backlog drains completely before the first polling delay"
        );
        helper.Batches.Enqueue(Batch(3, true));
        helper.Batches.Enqueue(Batch(4, true));
        helper.Batches.Enqueue(Batch(5, false));
        resume.TrySetResult();
        await Program.WaitUntilAsync(() => Volatile.Read(ref idleCalls) == 2, "Poll burst did not finish");
        Program.Check(
            recorder.Snapshot().ProcessTrace.Operations.Count == 6,
            "A later backlog also drains without an idle delay between batches"
        );
        helper.Batches.Enqueue(Batch(6, true));
        helper.Batches.Enqueue(
            new(
                new()
                {
                    State = "Completed",
                    StartedAt = now,
                    EndedAt = now.AddSeconds(1),
                    Shutdown = new()
                    {
                        CleanupCompletedAt = now.AddSeconds(1),
                        Current = new() { CallbacksFinished = 17 },
                    },
                },
                [],
                false
            )
        );
        await Task.WhenAll(trace.FinishAsync(), trace.FinishAsync());
        Program.Check(
            helper.Stops == 1
                && recorder.Snapshot().ProcessTrace.Operations.Count == 7
                && recorder.Snapshot().ProcessTrace.State == "Completed",
            "Concurrent finish calls stop once and retain the final buffered batch"
        );
        Program.Check(
            recorder.Snapshot().ProcessTrace.Shutdown?.CleanupCompletedAt == now.AddSeconds(1)
                && recorder.Snapshot().ProcessTrace.Shutdown?.Current.CallbacksFinished == 17,
            "Final IPC drain preserves shutdown metadata in the current session"
        );
    }
}
