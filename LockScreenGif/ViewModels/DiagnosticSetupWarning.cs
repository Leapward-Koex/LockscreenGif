using LockscreenGif.Models.Diagnostics;

namespace LockscreenGif.ViewModels;

/// <summary>Explains a preparation problem without exposing internal readiness states.</summary>
public sealed record DiagnosticSetupWarning(string Title, string Message)
{
    public static DiagnosticSetupWarning? FromCheck(DiagnosticCheck check)
    {
        if (check.Status is not ("Unavailable" or "Attention" or "Failed"))
        {
            return null;
        }

        return check.Name switch
        {
            "Session monitoring" => new(
                "Lock and unlock detection is unavailable",
                "Restart the app to try again. You can still run a test, but select Stop test after unlocking because the app may not detect your return."
            ),
            "Display-power monitoring" => new(
                "Screen power changes cannot be detected",
                "You can still test the animation, but the report will not record when your display turns off or wakes. Restart the app to try again."
            ),
            "Cache inventory" => new(
                "Windows lock-screen images could not be checked",
                "You can still start the test. It will request access if needed and report any problem applying the GIF."
            ),
            // Source selection already has an explanation next to its controls.
            // Trace availability is established during the test and appears in Findings.
            _ => null,
        };
    }
}
