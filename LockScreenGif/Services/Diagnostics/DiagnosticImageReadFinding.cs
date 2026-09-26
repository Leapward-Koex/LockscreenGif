using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;

namespace LockscreenGif.Services.Diagnostics;

/// <summary>Only reads that start after a copy was verified can establish access to that GIF.</summary>
internal static class DiagnosticImageReadFinding
{
    public static DiagnosticFinding? Create(DiagnosticSession session, IReadOnlyList<TraceAggregate> external)
    {
        if (session.ApplyResult is not { Files.Count: > 0 } apply)
        {
            return null;
        }

        var children = apply
            .Files.GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var reads = external
                    .Where(file =>
                        file.Reads > 0
                        && !file.IsApp
                        && file.AttributionResolved
                        && file.Path.Equals(group.Key, StringComparison.OrdinalIgnoreCase)
                    )
                    .ToArray();
                var verifiedAt = DiagnosticActivityTiming.VerifiedAt(session, group.Key);
                if (verifiedAt is null)
                {
                    return new DiagnosticFinding(
                        group.Key,
                        "A verified copy and its verification time are needed to identify reads of the applied GIF.",
                        "Info",
                        "Unverified timing"
                    );
                }

                var afterVerification = reads.Where(file => DiagnosticActivityTiming.HasReadAfterVerification(session, file)).ToArray();
                // PID 4 can service our own hash reads. It does not identify an independent reader.
                var applicationReads = afterVerification.Where(file => file.ProcessId > 4).ToArray();
                if (applicationReads.Length > 0)
                {
                    return new DiagnosticFinding(
                        group.Key,
                        string.Join(
                            "; ",
                            applicationReads.Select(file =>
                                $"{DiagnosticTraceFindings.Identity(file)}: completed a read after this GIF copy was verified."
                            )
                        ),
                        "Success",
                        "Observed"
                    );
                }

                var detail =
                    afterVerification.Length > 0
                        ? "Only System I/O was captured after verification. This can include the app's own inspection; an independent application read was not established."
                    : reads.Length > 0
                        ? "Earlier reads or reads without verified timing do not establish access to the applied GIF. Windows does not need to read every cached image."
                    : "No read captured for this variant after verification. Windows does not need to read every cached image.";
                return new DiagnosticFinding(group.Key, detail, "Info", "Not observed");
            })
            .ToList();
        var accessed = children.Any(child => child.Severity == "Success");
        return new DiagnosticFinding(
            "External image reads",
            accessed
                ? "An external application read an applied GIF after its copy was verified. Only one cached copy needs to be accessed; the other variants are optional. "
                    + "This confirms file access but cannot confirm visible animation."
                : "No independent application read of a verified GIF copy was captured after verification. This is inconclusive: Windows may reuse an image already in memory. "
                    + "If visible behavior is unexplained, compare selected and reference GIF runs and describe the screen and symptom.",
            accessed ? "Success" : "Info",
            accessed ? "Observed" : "Inconclusive"
        )
        {
            Children = children.OrderBy(child => child.Severity != "Success").ToList(),
        };
    }
}
