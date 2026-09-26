using LockscreenGif.Models;
using LockscreenGif.Models.Diagnostics;

namespace LockscreenGif.Services.Diagnostics;

/// <summary>Turns per-file evidence into checks without treating missing or stale evidence as a pass.</summary>
internal static class DiagnosticFileFindings
{
    internal static bool Matches(string? actual, string? expected) =>
        !string.IsNullOrWhiteSpace(expected)
        && !string.IsNullOrWhiteSpace(actual)
        && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    public static DiagnosticFinding Applied(LockscreenApplyResult apply, string? expected)
    {
        var children = apply
            .Files.Select(file =>
            {
                var passed = file.Copied && file.Verified && file.Error is null && Matches(file.Sha256, expected);
                var detail = passed
                    ? "Matches the selected GIF."
                    : file.Error
                        ?? (
                            !file.Copied ? "The image was not copied."
                            : string.IsNullOrWhiteSpace(expected) ? "The source hash is unavailable; the copy cannot be verified."
                            : "The destination did not verify against the selected GIF."
                        );
                return File(file.Path, detail, passed, expected, file.Sha256);
            })
            .ToList();
        var verified = children.Count(child => child.Severity == "Success");
        var passed = apply.Success && !apply.Cancelled && children.Count > 0 && verified == children.Count;
        return new(
            apply.Cancelled ? "Applying was cancelled"
                : passed ? "Copies verified"
                : "The GIF was not fully applied",
            $"{verified} of {children.Count} destinations matched the selected GIF. "
                + (
                    passed
                        ? "This does not confirm playback."
                        : (apply.Error ?? "Expand to review each image.")
                            + " Inspect the reported apply stage and destination in the report before repeating the test."
                ),
            passed ? "Success" : "Warning"
        )
        {
            Children = children,
        };
    }

    public static DiagnosticFinding? AfterUnlock(DiagnosticSession session)
    {
        var snapshot = session.Snapshots.LastOrDefault(item => item.Reason == "AfterUnlock");
        if (snapshot is null || session.ApplyResult is not { Files.Count: > 0 } apply)
        {
            return null;
        }

        var expected = session.Gif?.Sha256;
        var children = apply
            .Files.Select(target =>
            {
                var file = snapshot.Files.LastOrDefault(item => string.Equals(item.Path, target.Path, StringComparison.OrdinalIgnoreCase));
                if (file is null)
                {
                    return File(
                        target.Path,
                        snapshot.Complete
                        && target.VerifiedAt is { } missingBoundary
                        && missingBoundary != default
                        && snapshot.Timestamp >= missingBoundary
                            ? "The expected image was absent from the final inventory."
                            : "Not verified: the final inventory was incomplete or its timing after verification is unavailable.",
                        false,
                        expected,
                        null
                    );
                }

                if (!file.Stable || file.Error is not null)
                {
                    return File(
                        target.Path,
                        file.Error ?? "The image changed while being read; its contents could not be verified.",
                        false,
                        expected,
                        null
                    );
                }

                var fresh =
                    target.VerifiedAt is { } verifiedAt
                    && verifiedAt != default
                    && snapshot.Timestamp >= verifiedAt
                    && file.HashReadAt >= snapshot.Timestamp
                    && file.HashSource.StartsWith("Full read", StringComparison.Ordinal);
                var passed = fresh && Matches(file.Sha256, expected);
                return File(
                    target.Path,
                    !fresh ? "Not verified: a fresh read after verification and unlocking is required."
                        : passed ? "Still matches the selected GIF after unlocking."
                        : string.IsNullOrWhiteSpace(expected) ? "The source hash is unavailable; no comparison can be made."
                        : "The image does not match the selected GIF after unlocking.",
                    passed,
                    expected,
                    file.Sha256
                );
            })
            .ToList();
        var verified = children.Count(child => child.Severity == "Success");
        var complete = snapshot.Complete && session.LockObserved && session.UnlockObserved;
        var passed = complete && verified == children.Count;
        return new(
            passed ? "GIF retained after unlocking" : "Files after unlocking need attention",
            $"{verified} of {children.Count} images freshly verified. "
                + (
                    !complete
                        ? "The inventory or lock/unlock cycle was incomplete."
                        : "File checks do not confirm which image Windows displayed."
                ),
            passed ? "Success" : "Warning"
        )
        {
            Children = children,
        };
    }

    public static List<DiagnosticFinding> Changed(IEnumerable<string> paths, IEnumerable<CacheSnapshot> snapshots, string expected)
    {
        var evidence = snapshots.SelectMany(snapshot => snapshot.Files).ToList();
        return paths
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var file = evidence.LastOrDefault(item =>
                    string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)
                    && item.Stable
                    && item.Error is null
                    && !string.IsNullOrWhiteSpace(item.Sha256)
                    && !Matches(item.Sha256, expected)
                );
                return File(
                    path,
                    "Different bytes were observed after applying. Windows' use of this image is unknown.",
                    false,
                    expected,
                    file?.Sha256
                );
            })
            .ToList();
    }

    public static DiagnosticFinding File(string path, string detail, bool passed, string? expected, string? actual) =>
        new(path, detail + $"\nExpected SHA-256: {Value(expected)}\nObserved SHA-256: {Value(actual)}", passed ? "Success" : "Warning");

    private static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "Unavailable" : value;
}
