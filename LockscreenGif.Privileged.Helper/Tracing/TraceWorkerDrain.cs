using System.Diagnostics;

namespace LockscreenGif.Privileged.Helper.Tracing;

internal static class TraceWorkerDrain
{
    internal const string TimeoutReason =
        "File activity monitoring did not finish shutting down within five seconds. "
        + "Some final events may be missing; this does not mean the GIF failed.";

    public static async Task WaitAsync(
        Task worker,
        TraceProgressRecorder progress,
        Action forceStop,
        Action<string> incomplete,
        TimeSpan? deadline = null,
        TimeSpan? grace = null
    )
    {
        progress.DrainStarting();
        var started = Stopwatch.GetTimestamp();
        try
        {
            await worker.WaitAsync(deadline ?? TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            progress.DrainTimedOut(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            incomplete(TimeoutReason);
            progress.ForceStopRequested();
            forceStop();
            try
            {
                await worker.WaitAsync(grace ?? TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                progress.GraceExceeded();
                // Let the caller retrieve the partial report, including shutdown measurements.
                // The run still closes its helper connection and the helper owns final cleanup.
            }
        }
    }
}
