using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LockscreenGif.Services.Analytics;

// Never inspect messages, stack traces, Data, file names, or arbitrary exception type names.
internal static class AnalyticsErrorDetails
{
    public static AnalyticsProperties Apply(AnalyticsProperties properties, Exception exception)
    {
        var error = Unwrap(exception);
        var kind = ClassifyUnwrapped(error);
        return properties with
        {
            Outcome = kind == AnalyticsErrorKind.Cancelled ? AnalyticsOutcome.Cancelled : AnalyticsOutcome.Failed,
            ErrorKind = kind,
            ExceptionType = TypeOf(error),
            ErrorHResult = error.HResult,
            ErrorComponent = error is MediaProcessingException media ? media.Component : null,
            NativeErrorCode = error switch
            {
                MediaProcessingException mediaError => mediaError.ErrorCode,
                _ => Win32Code(error),
            },
        };
    }

    public static AnalyticsErrorKind Classify(Exception exception) => ClassifyUnwrapped(Unwrap(exception));

    private static AnalyticsErrorKind ClassifyUnwrapped(Exception error)
    {
        if (error is MediaProcessingException media)
        {
            // These are Gifski's public error discriminants, never parsed text.
            return (media.Component, media.ErrorCode) switch
            {
                (MediaProcessingComponent.Gifski, 2) => AnalyticsErrorKind.InvalidState,
                (MediaProcessingComponent.Gifski, 7) => AnalyticsErrorKind.PermissionDenied,
                (MediaProcessingComponent.Gifski, 9) => AnalyticsErrorKind.InvalidMedia,
                (MediaProcessingComponent.Gifski, 10) => AnalyticsErrorKind.Timeout,
                _ => AnalyticsErrorKind.NativeFailure,
            };
        }

        if (error is OperationCanceledException)
        {
            return AnalyticsErrorKind.Cancelled;
        }

        // A Win32Exception's HResult does not necessarily contain NativeErrorCode.
        var code = Win32Code(error) ?? error.HResult;
        var nativeKind = code switch
        {
            39 or 112 => AnalyticsErrorKind.DiskFull,
            8 or 14 => AnalyticsErrorKind.OutOfMemory,
            5 => AnalyticsErrorKind.PermissionDenied,
            // WinError.h: integrity policy, group policy, or signature verification blocked the file.
            4551 or 1260 or 577 => AnalyticsErrorKind.SecurityPolicyBlocked,
            // Mferror.h: MF_E_TOPO_CODEC_NOT_FOUND; does not identify which codec is missing.
            unchecked((int)0xC00D5212) => AnalyticsErrorKind.CodecMissing,
            _ => (AnalyticsErrorKind?)null,
        };
        return nativeKind
            ?? error switch
            {
                UnauthorizedAccessException or System.Security.SecurityException => AnalyticsErrorKind.PermissionDenied,
                InvalidDataException or FormatException => AnalyticsErrorKind.InvalidMedia,
                TimeoutException => AnalyticsErrorKind.Timeout,
                OutOfMemoryException => AnalyticsErrorKind.OutOfMemory,
                DllNotFoundException or EntryPointNotFoundException => AnalyticsErrorKind.DependencyMissing,
                BadImageFormatException => AnalyticsErrorKind.DependencyIncompatible,
                IOException => AnalyticsErrorKind.Io,
                Win32Exception or COMException => AnalyticsErrorKind.NativeFailure,
                InvalidOperationException => AnalyticsErrorKind.InvalidState,
                ArgumentException => AnalyticsErrorKind.InvalidArgument,
                _ => AnalyticsErrorKind.Other,
            };
    }

    private static int? Win32Code(Exception error)
    {
        if (error is Win32Exception win32)
        {
            return win32.NativeErrorCode;
        }

        // Only HRESULT_FROM_WIN32 failures contain a Win32 code in their low word.
        // E_FAIL, Media Foundation, and other facilities must keep their original HRESULT only.
        return ((uint)error.HResult & 0xFFFF0000U) == 0x80070000U ? error.HResult & 0xFFFF : null;
    }

    internal static Exception Unwrap(Exception exception)
    {
        // Only unwrap known containers with a single cause; bound the work on capture paths.
        for (var depth = 0; depth < 8; depth++)
        {
            var inner = exception switch
            {
                AggregateException aggregate when aggregate.InnerExceptions.Count == 1 => aggregate.InnerExceptions[0],
                TargetInvocationException or TypeInitializationException => exception.InnerException,
                _ => null,
            };
            if (inner is null)
            {
                break;
            }
            exception = inner;
        }
        return exception;
    }

    private static AnalyticsExceptionType TypeOf(Exception exception) =>
        exception switch
        {
            MediaProcessingException => AnalyticsExceptionType.MediaProcessing,
            OperationCanceledException => AnalyticsExceptionType.Cancelled,
            UnauthorizedAccessException => AnalyticsExceptionType.UnauthorizedAccess,
            System.Security.SecurityException => AnalyticsExceptionType.Security,
            InvalidDataException => AnalyticsExceptionType.InvalidData,
            FormatException => AnalyticsExceptionType.Format,
            TimeoutException => AnalyticsExceptionType.Timeout,
            FileNotFoundException => AnalyticsExceptionType.FileNotFound,
            DirectoryNotFoundException => AnalyticsExceptionType.DirectoryNotFound,
            IOException => AnalyticsExceptionType.Io,
            OutOfMemoryException => AnalyticsExceptionType.OutOfMemory,
            DllNotFoundException => AnalyticsExceptionType.DllNotFound,
            EntryPointNotFoundException => AnalyticsExceptionType.EntryPointNotFound,
            BadImageFormatException => AnalyticsExceptionType.BadImageFormat,
            Win32Exception => AnalyticsExceptionType.Win32,
            COMException => AnalyticsExceptionType.Com,
            InvalidOperationException => AnalyticsExceptionType.InvalidOperation,
            ArgumentException => AnalyticsExceptionType.Argument,
            AggregateException => AnalyticsExceptionType.Aggregate,
            _ => AnalyticsExceptionType.Other,
        };
}
