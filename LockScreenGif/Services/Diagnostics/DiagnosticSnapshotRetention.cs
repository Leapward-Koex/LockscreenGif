using LockscreenGif.Models.Diagnostics;

namespace LockscreenGif.Services.Diagnostics;

/// <summary>Bounds durable evidence while preserving initial and final diagnostic boundaries.</summary>
public static class DiagnosticSnapshotRetention
{
    public const int MaximumSnapshots = 60;
    public const int MaximumEnvironmentObservations = 32;
    public const string WarningMessage =
        "Diagnostic evidence limit reached. Older observations were omitted; "
        + "initial, after-apply, latest after-unlock, and recent evidence were retained. Monitoring coverage is incomplete.";

    /// <summary>
    /// Call inside DiagnosticRecorder.Update after appending evidence. Returns true only for the first
    /// eviction in this session; the caller should add one recorder warning with WarningMessage afterward.
    /// </summary>
    public static bool Enforce(DiagnosticSession session)
    {
        var omittedSnapshots = Trim(session.Snapshots, MaximumSnapshots, snapshot => snapshot.Reason);
        var omittedEnvironment = Trim(session.EnvironmentObservations, MaximumEnvironmentObservations, evidence => evidence.Reason);
        session.OmittedSnapshotCount += omittedSnapshots;
        session.OmittedEnvironmentObservationCount += omittedEnvironment;
        if (session.EvidenceTruncated)
        {
            session.MonitoringComplete = false;
        }

        if (omittedSnapshots == 0 && omittedEnvironment == 0)
        {
            return false;
        }

        var newlyTruncated = !session.EvidenceTruncated;
        session.EvidenceTruncated = true;
        session.MonitoringComplete = false;
        return newlyTruncated;
    }

    private static int Trim<T>(List<T> evidence, int maximum, Func<T, string> reason)
    {
        if (evidence.Count <= maximum)
        {
            return 0;
        }

        var originalCount = evidence.Count;
        // Protect the first observation, first baseline/apply boundaries, and latest final boundary.
        // Duplicate boundary labels cannot defeat the cap.
        var keep = new HashSet<int> { 0, evidence.Count - 1 };
        KeepIfPresent(keep, evidence.FindIndex(item => reason(item) == "Baseline"));
        KeepIfPresent(keep, evidence.FindIndex(item => reason(item) == "AfterApply"));
        KeepIfPresent(keep, evidence.FindLastIndex(item => reason(item) == "AfterUnlock"));
        for (var i = evidence.Count - 1; i >= 0 && keep.Count < maximum; i--)
        {
            keep.Add(i);
        }

        for (var i = evidence.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(i))
            {
                evidence.RemoveAt(i);
            }
        }

        return originalCount - evidence.Count;
    }

    private static void KeepIfPresent(HashSet<int> indices, int index)
    {
        if (index >= 0)
        {
            indices.Add(index);
        }
    }
}
