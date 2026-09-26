using LockscreenGif.Models.Diagnostics;

namespace LockscreenGif.Services.Diagnostics;

internal static class DiagnosticCacheFindings
{
    internal static IEnumerable<DiagnosticFinding> Analyze(DiagnosticSession session)
    {
        var targets = TraceHashCorrelation.VerifiedTargets(session);
        if (targets.Count == 0)
        {
            yield break;
        }

        var changed = new List<DiagnosticFinding>();
        var missing = new List<DiagnosticFinding>();
        foreach (var target in targets)
        {
            var mismatch = TraceHashCorrelation
                .FreshReads(session, target)
                .LastOrDefault(file => !DiagnosticFileFindings.Matches(file.Sha256, target.Sha256));
            if (mismatch is not null)
            {
                changed.Add(
                    DiagnosticFileFindings.File(
                        target.Path,
                        $"Different bytes were freshly read at {mismatch.HashReadAt:O}, after verification at {target.VerifiedAt:O}. "
                            + "Windows' use of this image is unknown.",
                        false,
                        target.Sha256,
                        mismatch.Sha256
                    )
                );
            }

            var absent = session.Snapshots.FirstOrDefault(snapshot =>
                snapshot.Reason != "Baseline"
                && snapshot.Complete
                && snapshot.Timestamp >= target.VerifiedAt
                && !snapshot.Files.Any(file => file.Path.Equals(target.Path, StringComparison.OrdinalIgnoreCase))
            );
            if (absent is not null)
            {
                missing.Add(
                    DiagnosticFileFindings.File(
                        target.Path,
                        $"The expected image was absent from a complete inventory at {absent.Timestamp:O}, after verification.",
                        false,
                        target.Sha256,
                        null
                    )
                );
            }
        }

        if (changed.Count > 0)
        {
            yield return new DiagnosticFinding(
                "Cache contents changed after applying",
                $"{changed.Count} verified file(s) later contained different bytes. These checks do not identify the displayed image. "
                    + "Compare a reference-animation run and include both exports if the visible result is unexpected.",
                "Warning"
            )
            {
                Children = changed.OrderBy(file => file.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            };
        }

        if (missing.Count > 0)
        {
            yield return new DiagnosticFinding(
                "Verified files disappeared",
                $"{missing.Count} applied file(s) were absent from a complete later inventory. "
                    + "Retain this export and compare a reference-animation run if the image changed unexpectedly.",
                "Warning"
            )
            {
                Children = missing.OrderBy(file => file.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            };
        }

        var intended = session.ApplyResult!.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var latestVerification = targets.Max(target => target.VerifiedAt);
        var variants = session
            .Snapshots.Where(snapshot => snapshot.Reason != "Baseline" && snapshot.Timestamp >= latestVerification)
            .SelectMany(snapshot => snapshot.Files.Where(file => TraceHashCorrelation.IsFresh(snapshot, file)))
            .Where(file =>
                !intended.Contains(file.Path)
                && file.Path.EndsWith("_notdimmed.jpg", StringComparison.OrdinalIgnoreCase)
                && !DiagnosticFileFindings.Matches(file.Sha256, session.Gif?.Sha256)
            )
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(file => file.HashReadAt).First())
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (variants.Length > 0)
        {
            yield return new DiagnosticFinding(
                "Additional cache variants found",
                $"{variants.Length} other resolution variant(s) did not match the GIF. Their use by Windows is unconfirmed.",
                "Info",
                "Inconclusive"
            )
            {
                Children = variants
                    .Select(file => new DiagnosticFinding(
                        file.Path,
                        "This optional variant contains different bytes; its use by Windows is unknown."
                            + $"\nSelected GIF SHA-256: {session.Gif!.Sha256}\nObserved SHA-256: {file.Sha256}",
                        "Info",
                        "Inconclusive"
                    ))
                    .ToList(),
            };
        }
    }
}
