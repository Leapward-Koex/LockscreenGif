using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LockscreenGif.Privileged.Helper.Tracing;

/// <summary>Owns only our fixed-GUID, real-time system logger. Construct/dispose on the consumer thread.</summary>
internal sealed class NativeTraceSession : IDisposable
{
    internal const string Name = "LockscreenGif.Diagnostics.FileIO";
    private static readonly Guid Identity = new("f312ab18-bef9-4f7b-884e-a24cdd36f310");
    private readonly Mutex _ownership = new(false, @"Global\LockscreenGif.Diagnostics.FileIO.Owner");
    private bool _owned;
    private ulong _handle;
    private readonly object _stopGate = new();

    public NativeTraceSession()
    {
        try
        {
            _owned = _ownership.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _owned = true;
        }
        if (!_owned)
        {
            _ownership.Dispose();
            throw new IOException("Another diagnostic trace is active.");
        }
        try
        {
            using var query = Properties();
            var result = ControlTrace(0, Name, query.Pointer, 0);
            if (result == 0)
            {
                var old = Marshal.PtrToStructure<TraceProperties>(query.Pointer);
                if (old.Guid != Identity || old.LogFileMode != 0x02000100 || old.LogFileNameOffset != 0)
                {
                    throw new IOException("An existing trace with this name has a different identity.");
                }

                Check(ControlTrace(0, Name, query.Pointer, 1));
            }
            else if (result != 4201)
            {
                Check(result);
            }

            using var properties = Properties();
            Check(StartTrace(out _handle, Name, properties.Pointer));
        }
        catch
        {
            _ownership.ReleaseMutex();
            _ownership.Dispose();
            _owned = false;
            throw;
        }
    }

    public void Enable(ulong flags) => Check(TraceSetInformation(_handle, 4, ref flags, 8));

    public NativeTraceStopResult Stop()
    {
        lock (_stopGate)
        {
            return StopCore();
        }
    }

    private NativeTraceStopResult StopCore()
    {
        if (_handle == 0)
        {
            return new(null, null);
        }

        using var properties = Properties();
        var result = ControlTrace(_handle, Name, properties.Pointer, 1);
        // Preserve the exact Windows result for the report before interpreting it.
        if (result is not (0 or 4201))
        {
            return new(result, null);
        }

        _handle = 0;
        if (result == 4201)
        {
            return new(result, null);
        }

        var stats = Marshal.PtrToStructure<TraceProperties>(properties.Pointer);
        return new(
            result,
            new(stats.NumberOfBuffers, stats.FreeBuffers, stats.BuffersWritten, stats.EventsLost, stats.RealTimeBuffersLost)
        );
    }

    public void Dispose()
    {
        try
        {
            Stop().EnsureSuccess();
        }
        finally
        {
            if (_owned)
            {
                _owned = false;
                _ownership.ReleaseMutex();
                _ownership.Dispose();
            }
        }
    }

    private static void Check(uint result)
    {
        if (result != 0)
        {
            throw new Win32Exception((int)result);
        }
    }

    private static NativeBuffer Properties()
    {
        var size = Marshal.SizeOf<TraceProperties>();
        var result = new NativeBuffer(size + 2048);
        Marshal.StructureToPtr(
            new TraceProperties
            {
                WnodeSize = (uint)(size + 2048),
                Guid = Identity,
                ClientContext = 1,
                WnodeFlags = 0x20000,
                BufferSize = 64,
                MinimumBuffers = 32,
                MaximumBuffers = 512,
                LogFileMode = 0x02000100,
                FlushTimer = 1,
                LoggerNameOffset = (uint)size,
            },
            result.Pointer,
            false
        );
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceProperties
    {
        public uint WnodeSize,
            ProviderId;
        public ulong HistoricalContext,
            TimeStamp;
        public Guid Guid;
        public uint ClientContext,
            WnodeFlags;
        public uint BufferSize,
            MinimumBuffers,
            MaximumBuffers,
            MaximumFileSize,
            LogFileMode,
            FlushTimer,
            EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers,
            FreeBuffers,
            EventsLost,
            BuffersWritten,
            LogBuffersLost,
            RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset,
            LoggerNameOffset;
    }

    private sealed class NativeBuffer : IDisposable
    {
        public IntPtr Pointer { get; }

        public NativeBuffer(int bytes)
        {
            Pointer = Marshal.AllocHGlobal(bytes);
            Marshal.Copy(new byte[bytes], 0, Pointer, bytes);
        }

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW")]
    private static extern uint StartTrace(out ulong handle, string name, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")]
    private static extern uint ControlTrace(ulong handle, string name, IntPtr properties, uint code);

    [DllImport("advapi32.dll")]
    private static extern uint TraceSetInformation(ulong handle, int informationClass, ref ulong information, uint length);
}
