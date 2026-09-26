using LockscreenGif.Models;
using LockscreenGif.Models.Diagnostics;

namespace LockscreenGif.Services.Diagnostics;

internal static class TraceHashCorrelation
{
    internal static IReadOnlyList<LockscreenFileResult> VerifiedTargets(DiagnosticSession session) =>
        session
            .ApplyResult?.Files.Where(file =>
                file.Copied
                && file.Verified
                && file.Error is null
                && file.VerifiedAt is not null
                && file.VerifiedAt != default(DateTimeOffset)
                && DiagnosticFileFindings.Matches(file.Sha256, session.Gif?.Sha256)
            )
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(file => file.VerifiedAt).First())
            .ToArray()
        ?? [];

    internal static bool IsFresh(CacheSnapshot snapshot, CacheFileEvidence file) =>
        snapshot.Timestamp != default
        && file.Stable
        && file.Error is null
        && !string.IsNullOrWhiteSpace(file.Sha256)
        && file.HashSource.StartsWith("Full read", StringComparison.Ordinal)
        && file.HashReadAt >= snapshot.Timestamp;

    internal static IReadOnlyList<CacheFileEvidence> FreshReads(DiagnosticSession session, LockscreenFileResult target) =>
        session
            .Snapshots.Where(snapshot => snapshot.Reason != "Baseline" && snapshot.Timestamp >= target.VerifiedAt)
            .SelectMany(snapshot =>
                snapshot.Files.Where(file => file.Path.Equals(target.Path, StringComparison.OrdinalIgnoreCase) && IsFresh(snapshot, file))
            )
            .OrderBy(file => file.HashReadAt)
            .ToArray();

    public static string Describe(DiagnosticSession session, string path)
    {
        var target = VerifiedTargets(session).FirstOrDefault(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return " A verified applied hash and its verification time are needed to compare later contents.";
        }

        var reads = FreshReads(session, target);
        var changed = reads.LastOrDefault(file => !DiagnosticFileFindings.Matches(file.Sha256, target.Sha256));
        if (changed is not null)
        {
            return $" A fresh hash at {changed.HashReadAt:O} differed from the GIF verified at {target.VerifiedAt:O}. "
                + "Activity in this interval does not establish which operation produced the different bytes.";
        }

        return reads.Count > 0
            ? " Fresh post-verification hashes matched the applied GIF. Writes alone do not establish replacement."
            : " No fresh post-verification hash was available; writes alone do not establish replacement.";
    }
}
