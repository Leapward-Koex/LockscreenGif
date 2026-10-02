using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;

namespace LockscreenGif.Services.Diagnostics;

internal static class LockscreenReadVerification
{
    public static LockscreenVerificationResult CreateResult(DiagnosticSession session, int sessionId, bool timedOut)
    {
        if (HasConfirmedRead(session, sessionId))
        {
            return new(true, true, "Lock-screen read confirmed", "LogonUI read the copied GIF after it was applied.");
        }

        var message =
            timedOut ? "Monitoring reached its time limit without confirming a LogonUI read. The GIF was applied."
            : session.ProcessTrace.State != "Completed" || session.ProcessTrace.HasCollectionGaps
                ? "File monitoring was incomplete, so a LogonUI read could not be confirmed. The GIF was applied."
            : "No LogonUI read was captured. Windows may have reused a cached image. The GIF was applied.";
        return new(false, true, "Lock-screen read not confirmed", message);
    }

    internal static bool HasConfirmedRead(DiagnosticSession session, int sessionId)
    {
        if (
            session.ProcessTrace.Files.Any(file =>
                IsLogonUi(file.ProcessName, file.ProcessId, file.IsApp, file.AttributionResolved, file.SessionId, sessionId)
                && file.Reads > 0
                && file.ReadBytes > 0
                && DiagnosticActivityTiming.HasReadAfterVerification(session, file)
            )
        )
        {
            return true;
        }

        // A retained operation can still prove the read when its aggregate was omitted.
        return session.ProcessTrace.Operations.Any(operation =>
            IsLogonUi(
                operation.ProcessName,
                operation.ProcessId,
                operation.IsApp,
                operation.AttributionResolved,
                operation.SessionId,
                sessionId
            )
            && operation.Operation == "Read"
            && operation.Succeeded
            && operation.CompletedBytes > 0
            && operation.Timestamp != default
            && operation.CompletedAt >= operation.Timestamp
            && DiagnosticActivityTiming.VerifiedAt(session, operation.Path) is { } verifiedAt
            && operation.Timestamp >= verifiedAt
        );
    }

    private static bool IsLogonUi(string name, int processId, bool isApp, bool resolved, int? actualSession, int expectedSession) =>
        processId > 4
        && !isApp
        && resolved
        && actualSession == expectedSession
        && (name.Equals("LogonUI.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("LogonUI", StringComparison.OrdinalIgnoreCase));
}
