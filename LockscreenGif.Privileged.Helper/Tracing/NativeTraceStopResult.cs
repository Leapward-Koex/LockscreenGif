using System.ComponentModel;
using LockscreenGif.Privileged;

namespace LockscreenGif.Privileged.Helper.Tracing;

internal sealed record NativeTraceStopResult(uint? Status, TraceSessionBufferStatistics? Buffers)
{
    public bool Attempted => Status is not null;
    public bool Succeeded => Status is null or 0 or 4201;
    public long EventsLost => Buffers is { } stats ? stats.EventsLost + (long)stats.RealTimeBuffersLost : 0;

    public void EnsureSuccess()
    {
        if (!Succeeded)
        {
            throw new Win32Exception((int)Status!.Value);
        }
    }
}
