using System.Text;
using LockscreenGif.Privileged;

namespace LockscreenGif.Services.Diagnostics;

internal static class DiagnosticTraceShutdownSummary
{
    public static void AppendTo(StringBuilder text, TraceShutdownEvidence? shutdown)
    {
        if (shutdown is null)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine($"Trace worker stage: {shutdown.WorkerStage}.");
        text.AppendLine(
            $"Stop requested: {At(shutdown.StopRequestedAt)}; Windows stop returned: {At(shutdown.NativeStopReturnedAt)}; "
                + $"consumer returned: {At(shutdown.ConsumerReturnedAt)}; cleanup completed: {At(shutdown.CleanupCompletedAt)}."
        );
        if (shutdown.NativeStopAttempted == false)
        {
            text.AppendLine("Native stop call skipped: this session was already stopped.");
        }

        if (shutdown.NativeStopStatus is { } status)
        {
            text.AppendLine($"Windows stop result: {status} (0x{status:X8}); elapsed: {shutdown.NativeStopElapsedMilliseconds:0.##} ms.");
        }

        if (shutdown.NativeStopFailureType is { } failure)
        {
            text.AppendLine($"Windows stop exception: {failure}.");
        }

        text.AppendLine($"Consumer completed normally: {shutdown.ConsumerCompletedNormally?.ToString() ?? "Not recorded"}.");
        if (shutdown.ConsumerFailureType is { } consumerFailure)
        {
            text.AppendLine($"Consumer exception: {consumerFailure}.");
        }

        Progress(text, "Current consumer progress", shutdown.Current);
        Progress(text, "At stop request", shutdown.AtStopRequest);
        Progress(text, "At Windows stop return", shutdown.AtNativeStopReturn);
        if (shutdown.DrainDeadlineExceededAt is { } deadline)
        {
            text.AppendLine(
                $"Drain deadline reached: {deadline:O}; worker stage then: {shutdown.StageAtDeadline}; "
                    + $"wait elapsed: {shutdown.DrainWaitElapsedMilliseconds:0.##} ms."
            );
            Progress(text, "At drain deadline", shutdown.AtDrainDeadline);
        }
        if (shutdown.ForceStopRequestedAt is { } forced)
        {
            text.AppendLine($"Forced consumer stop requested: {forced:O}.");
        }

        if (shutdown.ForcedStopGraceExceededAt is { } grace)
        {
            text.AppendLine($"Consumer still unfinished after forced-stop grace period: {grace:O}.");
        }

        if (shutdown.NativeStopBuffers is { } buffers)
        {
            text.AppendLine(
                $"Native session buffers at stop: {buffers.BufferCount} allocated, {buffers.FreeBuffers} free, "
                    + $"{buffers.BuffersWritten} written/delivered; events lost {buffers.EventsLost}, real-time buffers lost {buffers.RealTimeBuffersLost}. "
                    + "These are session statistics, not counts of buffers processed by the consumer."
            );
        }

        text.AppendLine(
            "Consumer progress counts all dispatched ETW events, including unrelated activity. "
                + "No unrelated payloads are retained. Worker cleanup time does not establish event coverage through that time."
        );
    }

    private static void Progress(StringBuilder text, string label, TraceConsumerProgress? progress)
    {
        if (progress is null)
        {
            return;
        }

        text.AppendLine(
            $"{label}: {progress.CallbacksStarted} callbacks started, {progress.CallbacksFinished} finished; "
                + $"callback in progress: {progress.CallbackInProgress}; latest event timestamp: {At(progress.LatestEventTimestamp)}; "
                + $"last callback started: {At(progress.LastCallbackStartedAt)}; finished: {At(progress.LastCallbackFinishedAt)}."
        );
    }

    private static string At(DateTimeOffset? value) => value?.ToString("O") ?? "Not recorded";
}
