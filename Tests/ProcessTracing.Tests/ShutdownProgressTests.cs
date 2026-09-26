using LockscreenGif.Privileged;
using LockscreenGif.Privileged.Helper.Tracing;

namespace ProcessTracing.Tests;

internal static class ShutdownProgressTests
{
    public static async Task RunAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var progress = new TraceProgressRecorder();
        progress.ConsumerStarting();
        progress.DispatchStarted(now);
        progress.StopRequested();
        var first = progress.Snapshot();
        progress.DispatchFinished();
        progress.DispatchStarted(now.AddSeconds(-1));
        progress.DispatchFinished();
        progress.NativeStopStarting();
        progress.NativeStopReturned(new(0, new(32, 30, 4, 0, 0)), 12);
        var snapshot = progress.Snapshot();
        Program.Check(
            first.AtStopRequest!.CallbackInProgress
                && first.Current.CallbacksFinished == 0
                && snapshot.Current.CallbacksFinished == 2
                && snapshot.Current.LatestEventTimestamp == now,
            "Progress checkpoints are immutable and out-of-order events do not move latest event time backwards"
        );
        Program.Check(
            snapshot.ConsumerReturnedAt is null
                && snapshot.CleanupCompletedAt is null
                && snapshot.NativeStopStatus == 0
                && snapshot.NativeStopElapsedMilliseconds == 12
                && snapshot.NativeStopBuffers!.BuffersWritten == 4,
            "Native stop metadata is separate from consumer return and cleanup"
        );

        var forced = 0;
        var reasons = new List<string>();
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await TraceWorkerDrain.WaitAsync(
            worker.Task,
            progress,
            () =>
            {
                forced++;
                progress.ConsumerReturned(false);
                progress.CleanupStarting();
                progress.CleanupCompleted();
                worker.TrySetResult();
            },
            reasons.Add,
            TimeSpan.Zero
        );
        snapshot = progress.Snapshot();
        Program.Check(
            forced == 1
                && reasons.SequenceEqual(new[] { TraceWorkerDrain.TimeoutReason })
                && snapshot.StageAtDeadline == "Processing"
                && snapshot.AtDrainDeadline!.CallbacksFinished == 2
                && snapshot.DrainDeadlineExceededAt is not null
                && snapshot.ForceStopRequestedAt is not null
                && snapshot.ConsumerCompletedNormally == false
                && snapshot.CleanupCompletedAt is not null,
            "Drain timeout captures progress before forced stop and preserves incomplete reason after cleanup"
        );
        Program.Check(snapshot.ForcedStopGraceExceededAt is null, "Consumer completing after forced stop does not report a grace timeout");

        var completed = new TraceProgressRecorder();
        completed.ConsumerStarting();
        completed.ConsumerReturned(true);
        completed.CleanupStarting();
        completed.CleanupCompleted();
        await TraceWorkerDrain.WaitAsync(
            Task.CompletedTask,
            completed,
            () => throw new Exception("Must not force a finished worker"),
            _ => throw new Exception("Must not warn"),
            TimeSpan.Zero
        );
        Program.Check(
            completed.Snapshot().DrainDeadlineExceededAt is null && completed.Snapshot().ConsumerCompletedNormally == true,
            "Normal shutdown does not add a timeout or force-stop warning"
        );

        var stuck = new TraceProgressRecorder();
        stuck.ConsumerStarting();
        stuck.ConsumerReturned(true);
        stuck.CleanupStarting();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await TraceWorkerDrain.WaitAsync(pending.Task, stuck, () => { }, _ => { }, TimeSpan.Zero, TimeSpan.Zero);
        Program.Check(
            stuck.Snapshot().StageAtDeadline == "Disposing"
                && stuck.Snapshot().ForcedStopGraceExceededAt is not null
                && stuck.Snapshot().CleanupCompletedAt is null,
            "Cleanup delay is distinguishable from processing delay and grace timeout permits partial-report retrieval"
        );
        pending.SetResult();

        var failedStop = new NativeTraceStopResult(5, null);
        try
        {
            failedStop.EnsureSuccess();
            throw new Exception("Must fail");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Program.Check(ex.NativeErrorCode == 5, "Native stop preserves exact Windows error");
        }
        var failed = new TraceProgressRecorder();
        failed.NativeStopStarting();
        failed.NativeStopReturned(failedStop, 3, "Win32Exception");
        Program.Check(
            failed.Snapshot().NativeStopStatus == 5 && failed.Snapshot().NativeStopBuffers is null,
            "Failed native stop has an error code without fabricated zero buffer statistics"
        );
        Program.Check(
            new NativeTraceStopResult(null, null) is { Attempted: false, Succeeded: true, Status: null },
            "Idempotent cleanup does not invent a Windows result when no native call ran"
        );
        Program.Check(
            new NativeTraceStopResult(4201, null).Succeeded,
            "Already-stopped native session remains an accepted cleanup outcome"
        );
    }
}
