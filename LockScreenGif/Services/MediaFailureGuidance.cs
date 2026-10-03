using System.Runtime.InteropServices;
using LockscreenGif.Services.Analytics;

namespace LockscreenGif.Services;

internal static class MediaFailureGuidance
{
    // Recovery only replaces the Windows decoder. Access and policy failures need
    // to remain visible, and cancellation must never start another decoder.
    public static bool CanUseVideoFallback(Exception exception) =>
        exception is COMException or InvalidDataException or TimeoutException
        && AnalyticsProperties.ClassifyError(exception)
            is AnalyticsErrorKind.CodecMissing
                or AnalyticsErrorKind.NativeFailure
                or AnalyticsErrorKind.InvalidMedia
                or AnalyticsErrorKind.Timeout;

    public static string Message(Exception exception, string fallback) =>
        AnalyticsProperties.ClassifyError(exception) switch
        {
            AnalyticsErrorKind.SecurityPolicyBlocked =>
                "Windows security blocked a file needed for this operation. Update or reinstall LockscreenGif from its official source. "
                    + "If this PC is managed, ask your administrator to review the block.",
            AnalyticsErrorKind.CodecMissing =>
                "Windows could not find a compatible codec for this video. Try a video exported as MP4 with H.264 video.",
            AnalyticsErrorKind.PermissionDenied =>
                "The file or working folder could not be accessed. Check its permissions, or choose a readable local copy.",
            AnalyticsErrorKind.DiskFull =>
                "There is not enough free space to finish. Free space on the drive containing the app's temporary files and try again.",
            AnalyticsErrorKind.OutOfMemory => "There is not enough memory to finish. Try a shorter clip or a lower output resolution.",
            _ => fallback,
        };
}
