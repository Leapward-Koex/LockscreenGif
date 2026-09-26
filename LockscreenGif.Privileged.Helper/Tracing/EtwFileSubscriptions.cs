using LockscreenGif.Privileged;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace LockscreenGif.Privileged.Helper.Tracing;

internal static class EtwFileSubscriptions
{
    public const KernelTraceEventParser.Keywords Keywords =
        KernelTraceEventParser.Keywords.Process
        | KernelTraceEventParser.Keywords.Thread
        | KernelTraceEventParser.Keywords.DiskFileIO
        | KernelTraceEventParser.Keywords.FileIO
        | KernelTraceEventParser.Keywords.FileIOInit;

    public static void Attach(
        ETWTraceEventSource source,
        TraceIdentityMap identities,
        FileOperationCorrelator correlator,
        object gate,
        TraceProgressRecorder progress
    )
    {
        // Count dispatch progress without reading or retaining unrelated event payloads.
        source.AddDispatchHook(
            (e, dispatch) =>
            {
                progress.DispatchStarted(e.TimeStamp.ToUniversalTime());
                try
                {
                    dispatch(e);
                }
                finally
                {
                    progress.DispatchFinished();
                }
            }
        );
        var parser = new KernelTraceEventParser(source, KernelTraceEventParser.ParserTrackingOptions.None);
        void Guard(Action action)
        {
            lock (gate)
            {
                action();
            }
        }
        parser.ProcessStartGroup += e =>
            Guard(() =>
                identities.ProcessStart(
                    e.ProcessID,
                    e.ParentID,
                    e.ImageFileName,
                    e.SessionID,
                    e.TimeStamp.ToUniversalTime(),
                    e.Opcode != TraceEventOpcode.Start
                )
            );
        parser.ProcessStop += e => Guard(() => identities.ProcessEnd(e.ProcessID));
        parser.ThreadStartGroup += e => Guard(() => identities.ThreadStart(e.ThreadID, e.ProcessID, e.TimeStamp.ToUniversalTime()));
        parser.ThreadStop += e => Guard(() => identities.ThreadEnd(e.ThreadID));
        void Name(FileIONameTraceData e) => Guard(() => correlator.NameFile(e.FileKey, e.FileName, e.TimeStamp.ToUniversalTime()));
        parser.FileIOName += Name;
        parser.FileIOFileCreate += Name;
        parser.FileIOFileRundown += Name;
        parser.FileIOFileDelete += e => Guard(() => correlator.ForgetName(e.FileKey));
        parser.FileIOCreate += e =>
            Guard(() =>
                correlator.Begin(
                    e.IrpPtr,
                    e.FileObject,
                    0,
                    "Open",
                    e.ProcessID,
                    e.ThreadID,
                    e.TimeStamp.ToUniversalTime(),
                    rawPath: e.FileName
                )
            );
        void ReadWrite(FileIOReadWriteTraceData e, string operation) =>
            Guard(() =>
                correlator.Begin(
                    e.IrpPtr,
                    e.FileObject,
                    e.FileKey,
                    operation,
                    e.ProcessID,
                    e.ThreadID,
                    e.TimeStamp.ToUniversalTime(),
                    (uint)e.IoSize
                )
            );
        parser.FileIORead += e => ReadWrite(e, "Read");
        parser.FileIOWrite += e => ReadWrite(e, "Write");
        void Info(FileIOInfoTraceData e, string operation) =>
            Guard(() =>
                correlator.Begin(e.IrpPtr, e.FileObject, e.FileKey, operation, e.ProcessID, e.ThreadID, e.TimeStamp.ToUniversalTime())
            );
        parser.FileIORename += e => Info(e, "Rename");
        parser.FileIODelete += e => Info(e, "Delete");
        parser.FileIOClose += e => Guard(() => correlator.Close(e.FileObject));
        parser.FileIOOperationEnd += e =>
            Guard(() => correlator.End(e.IrpPtr, unchecked((uint)e.NtStatus), (long)e.ExtraInfo, e.TimeStamp.ToUniversalTime()));
    }
}
