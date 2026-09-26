using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;

namespace LockscreenGif.Services.Diagnostics;

internal enum DiagnosticActivityPhase
{
    Unknown,
    BeforeVerification,
    OverlapsVerification,
    AfterVerification,
}

internal enum DiagnosticActivityKind
{
    Failure,
    Modification,
    FastIoFallback,
}

internal sealed record DiagnosticActivityClassification(DiagnosticActivityPhase Phase, TraceActivityWitness? Witness = null);

/// <summary>Relates operation evidence to the verified generation of a particular applied file.</summary>
internal static class DiagnosticActivityTiming
{
    public static DateTimeOffset? VerifiedAt(DiagnosticSession session, string path) =>
        session
            .ApplyResult?.Files.Where(file =>
                file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
                && file.Copied
                && file.Verified
                && file.Error is null
                && file.VerifiedAt != default(DateTimeOffset)
            )
            .Select(file => file.VerifiedAt)
            .DefaultIfEmpty()
            .Max();

    public static IEnumerable<TraceOperation> Operations(ProcessTraceEvidence trace, TraceAggregate file) =>
        trace.Operations.Where(operation =>
            operation.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)
            && operation.ProcessId == file.ProcessId
            && operation.ProcessInstance == file.ProcessInstance
        );

    public static DiagnosticActivityClassification Classify(
        DiagnosticSession session,
        TraceAggregate file,
        DiagnosticActivityKind kind,
        bool allowBeforeVerification = true
    )
    {
        if (VerifiedAt(session, file.Path) is not { } boundary)
        {
            return new(DiagnosticActivityPhase.Unknown);
        }

        var trace = session.ProcessTrace;
        var retained = Operations(trace, file).ToArray();
        var matching = retained.Where(operation => Matches(operation, kind)).ToArray();
        var rawTiming = matching.Aggregate(new TraceActivityTiming(), (timing, operation) => timing.Add(operation));
        var summary = kind switch
        {
            DiagnosticActivityKind.Failure => file.FailureTiming,
            DiagnosticActivityKind.Modification => file.ModificationTiming,
            _ => null,
        };
        // Positive witnesses survive collection gaps and raw-record retention limits.
        var witnesses = new[] { summary?.LatestStarted, summary?.LatestCompleted, rawTiming.LatestStarted, rawTiming.LatestCompleted }
            .Where(witness => witness is not null && IsValid(witness) && Matches(witness, kind))
            .Select(witness => witness!)
            .ToArray();
        var after = witnesses
            .Where(witness => witness.StartedAt >= boundary)
            .OrderByDescending(witness => witness.StartedAt)
            .FirstOrDefault();
        if (after is not null)
        {
            return new(DiagnosticActivityPhase.AfterVerification, after);
        }

        var overlap = witnesses
            .Where(witness => witness.StartedAt < boundary && witness.CompletedAt >= boundary)
            .OrderByDescending(witness => witness.CompletedAt)
            .FirstOrDefault();
        if (overlap is not null)
        {
            return new(DiagnosticActivityPhase.OverlapsVerification, overlap);
        }

        if (!allowBeforeVerification)
        {
            return new(DiagnosticActivityPhase.Unknown);
        }

        // New summaries cover all processed operations, before detail retention limits.
        // Legacy raw records can support an all-before conclusion only with full coverage
        // and a category total that reconciles with the original aggregate semantics.
        var completeTiming = summary ?? (HasCompleteRawCategory(trace, file, kind, retained) ? rawTiming : null);
        if (
            completeTiming is { UntimedCount: 0, LatestStarted: { } started, LatestCompleted: { } completed }
            && IsValid(started)
            && Matches(started, kind)
            && IsValid(completed)
            && Matches(completed, kind)
            && completed.CompletedAt < boundary
        )
        {
            return new(DiagnosticActivityPhase.BeforeVerification, completed);
        }

        return new(DiagnosticActivityPhase.Unknown);
    }

    public static bool HasReadAfterVerification(DiagnosticSession session, TraceAggregate file)
    {
        if (VerifiedAt(session, file.Path) is not { } boundary)
        {
            return false;
        }

        if (
            file.LastReadStartedAt is { } started
            && file.LastReadCompletedAt is { } completed
            && started != default
            && started >= boundary
            && completed >= started
        )
        {
            return true;
        }

        return Operations(session.ProcessTrace, file)
            .Any(operation =>
                operation.Operation == "Read"
                && operation.Succeeded
                && operation.CompletedBytes > 0
                && operation.Timestamp != default
                && operation.Timestamp >= boundary
                && operation.CompletedAt >= operation.Timestamp
            );
    }

    public static bool HasCompleteRawCategory(
        ProcessTraceEvidence trace,
        TraceAggregate file,
        DiagnosticActivityKind kind,
        IReadOnlyList<TraceOperation>? retained = null
    )
    {
        if (trace.State != "Completed" || trace.HasCollectionGaps)
        {
            return false;
        }

        retained ??= Operations(trace, file).ToArray();
        var reconciled = retained.Where(operation =>
            kind is DiagnosticActivityKind.Failure or DiagnosticActivityKind.FastIoFallback && file.FastIoFallbacks is null
                ? operation.IsFailure || operation.IsFastIoFallback
                : Matches(operation, kind)
        );
        if (
            reconciled.Any(operation =>
                operation.Timestamp == default || operation.CompletedAt is not { } completed || completed < operation.Timestamp
            )
        )
        {
            return false;
        }

        return kind switch
        {
            DiagnosticActivityKind.Failure => retained.LongCount(operation =>
                operation.IsFailure || (file.FastIoFallbacks is null && operation.IsFastIoFallback)
            ) == file.Failures,
            DiagnosticActivityKind.Modification => retained.LongCount(operation => Matches(operation, kind)) == file.Modifications,
            DiagnosticActivityKind.FastIoFallback => file.FastIoFallbacks is { } count
                ? retained.LongCount(operation => operation.IsFastIoFallback) == count
                : retained.LongCount(operation => operation.IsFailure || operation.IsFastIoFallback) == file.Failures,
            _ => false,
        };
    }

    private static bool IsValid(TraceActivityWitness witness) =>
        witness.StartedAt != default && witness.CompletedAt >= witness.StartedAt && witness.Status != 0x103;

    private static bool Matches(TraceOperation operation, DiagnosticActivityKind kind) =>
        kind switch
        {
            DiagnosticActivityKind.Failure => operation.IsFailure,
            DiagnosticActivityKind.Modification => operation.Succeeded && operation.Operation is "Write" or "Rename" or "Delete",
            DiagnosticActivityKind.FastIoFallback => operation.IsFastIoFallback,
            _ => false,
        };

    private static bool Matches(TraceActivityWitness witness, DiagnosticActivityKind kind) =>
        kind switch
        {
            DiagnosticActivityKind.Failure => (witness.Status & 0x80000000) != 0 && witness.Status != 0xc01c0004,
            DiagnosticActivityKind.Modification => (witness.Status & 0x80000000) == 0
                && witness.Status != 0x103
                && witness.Operation is "Write" or "Rename" or "Delete",
            DiagnosticActivityKind.FastIoFallback => witness.Status == 0xc01c0004,
            _ => false,
        };
}
