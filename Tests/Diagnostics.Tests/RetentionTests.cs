using System.Text.Json;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Services.Diagnostics;

namespace Diagnostics.Tests;

internal static class RetentionTests
{
    internal static void Run()
    {
        var session = new DiagnosticSession();
        Add(session, "Baseline");
        Add(session, "AfterApply");
        Program.Check(
            !DiagnosticSnapshotRetention.Enforce(session) && session.MonitoringComplete,
            "Evidence below retention limits remains complete"
        );
        var baseline = session.Snapshots[0];
        var applied = session.Snapshots[1];
        var firstEnvironment = session.EnvironmentObservations[0];
        var warnings = 0;
        for (var i = 0; i < 150; i++)
        {
            Add(session, "Change " + i);
            if (DiagnosticSnapshotRetention.Enforce(session))
            {
                warnings++;
            }
        }
        Program.Check(
            session.Snapshots.Count == 60 && session.EnvironmentObservations.Count == 32,
            "Snapshot and environment storm retention is bounded"
        );
        Program.Check(
            session.Snapshots.Contains(baseline)
                && session.Snapshots.Contains(applied)
                && session.EnvironmentObservations.Contains(firstEnvironment)
                && session.Snapshots[^1].Reason == "Change 149",
            "Retention preserves initial/apply boundaries and latest evidence"
        );
        Program.Check(
            warnings == 1 && session.EvidenceTruncated && !session.MonitoringComplete,
            "First eviction requests one warning and marks monitoring incomplete"
        );
        Program.Check(
            session.OmittedSnapshotCount == 92 && session.OmittedEnvironmentObservationCount == 120,
            "Omitted observation counts remain available for exported evidence"
        );

        Add(session, "AfterUnlock");
        var unlocked = session.Snapshots[^1];
        var unlockedEnvironment = session.EnvironmentObservations[^1];
        if (DiagnosticSnapshotRetention.Enforce(session))
        {
            warnings++;
        }

        for (var i = 0; i < 80; i++)
        {
            Add(session, "Late change " + i);
            if (DiagnosticSnapshotRetention.Enforce(session))
            {
                warnings++;
            }
        }
        Program.Check(
            session.Snapshots.Contains(unlocked)
                && session.EnvironmentObservations.Contains(unlockedEnvironment)
                && session.Snapshots[^1].Reason == "Late change 79",
            "After-unlock evidence survives subsequent event storms alongside latest observation"
        );
        var clone = JsonSerializer.Deserialize<DiagnosticSession>(JsonSerializer.Serialize(session))!;
        Add(clone, "Recovered observation");
        Program.Check(
            !DiagnosticSnapshotRetention.Enforce(clone) && clone.EvidenceTruncated && !clone.MonitoringComplete && warnings == 1,
            "Truncation and one-time warning state survive persistence"
        );

        var oversized = new DiagnosticSession();
        for (var i = 0; i < 100; i++)
        {
            Add(oversized, "Baseline");
        }

        for (var i = 0; i < 100; i++)
        {
            Add(oversized, "AfterApply");
        }

        Add(oversized, "AfterUnlock");
        Program.Check(
            DiagnosticSnapshotRetention.Enforce(oversized)
                && oversized.Snapshots.Count == 60
                && oversized.EnvironmentObservations.Count == 32
                && oversized.Snapshots[^1].Reason == "AfterUnlock",
            "Duplicate boundary labels cannot bypass evidence limits"
        );
    }

    private static void Add(DiagnosticSession session, string reason)
    {
        session.Snapshots.Add(new() { Reason = reason });
        session.EnvironmentObservations.Add(new(DateTimeOffset.UtcNow, reason, new()));
    }
}
