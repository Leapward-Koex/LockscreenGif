using System.ComponentModel;
using LockscreenGif.Contracts.Services;
using LockscreenGif.Services.Analytics;

namespace LockscreenGif.Services.Diagnostics;

internal static class DiagnosticErrorReporting
{
    public static bool IsCancellation(Exception exception) =>
        exception is OperationCanceledException or Win32Exception { NativeErrorCode: 1223 }
        || exception.HResult == unchecked((int)0x800704C7);

    public static void Capture(IErrorReporter? reporter, Exception exception, AnalyticsErrorContext context, AnalyticsWorkflow workflow)
    {
        if (reporter is null || IsCancellation(exception))
        {
            return;
        }

        try
        {
            reporter.CaptureException(exception, context, workflow);
        }
        catch
        {
            // Error reporting must never prevent collecting or completing diagnostics.
        }
    }
}
