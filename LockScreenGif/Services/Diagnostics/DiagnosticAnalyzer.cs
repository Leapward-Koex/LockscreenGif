using LockscreenGif.Models.Diagnostics;

namespace LockscreenGif.Services.Diagnostics;

public static class DiagnosticAnalyzer
{
    public static List<DiagnosticFinding> Analyze(DiagnosticSession session)
    {
        var findings = new List<DiagnosticFinding>();
        if (!string.IsNullOrEmpty(session.Error))
        {
            findings.Add(new("Test needs attention", session.Error, "Warning"));
        }

        if (session.Gif is { Error: not null } gifError)
        {
            findings.Add(new("Source inspection failed", gifError.Error!, "Error"));
        }
        else if (session.Gif is { IsAnimated: false })
        {
            findings.Add(new("Source is not a multi-frame GIF", "Use an animated source or run the reference-animation test.", "Error"));
        }

        if (session.ApplyResult is { } apply)
        {
            findings.Add(DiagnosticFileFindings.Applied(apply, session.Gif?.Sha256));
        }

        findings.AddRange(DiagnosticCacheFindings.Analyze(session));
        if (DiagnosticFileFindings.AfterUnlock(session) is { } afterUnlock)
        {
            findings.Add(afterUnlock);
        }

        if (!session.LockObserved || !session.UnlockObserved)
        {
            findings.Add(
                new(
                    "Lock/unlock cycle incomplete",
                    "Both events must be observed in this Windows session for a complete test. Repeat the lock/unlock cycle and retain the export if it remains incomplete.",
                    "Warning"
                )
            );
        }
        else
        {
            findings.Add(new("Lock/unlock cycle observed", "Windows reported both locking and unlocking during this test.", "Success"));
        }

        if (!session.MonitoringComplete || session.Snapshots.Any(s => !s.Complete))
        {
            findings.Add(
                new(
                    "Monitoring has gaps",
                    "Some evidence was unavailable, unstable, or outside the collection budget. Unobserved changes cannot be ruled out. Repeat the capture and retain the export if the gap recurs.",
                    "Warning"
                )
            );
        }

        if (!string.IsNullOrWhiteSpace(session.Observation))
        {
            findings.Add(
                new(
                    "Your observation",
                    session.Observation + (string.IsNullOrWhiteSpace(session.ObservationSurface) ? "" : $" — {session.ObservationSurface}"),
                    session.Observation == "Animated correctly" ? "Success" : "Warning",
                    "User reported"
                )
            );
        }

        if (session.ComparisonSessionId is not null)
        {
            findings.Add(
                new(
                    "Comparison baseline may differ",
                    "An earlier API-on run may have changed Windows settings and cache state. This run does not automatically reset that state.",
                    "Info",
                    "Experimental"
                )
            );
        }

        findings.AddRange(DiagnosticTraceFindings.Analyze(session));
        return findings;
    }
}
