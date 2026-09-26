using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;

namespace LockscreenGif.Services.Diagnostics;

internal static class DiagnosticTraceFindings
{
    public static IEnumerable<DiagnosticFinding> Analyze(DiagnosticSession session)
    {
        var trace = session.ProcessTrace;
        if (trace.State == "NotStarted")
        {
            yield break;
        }

        var complete = trace.State == "Completed" && !trace.HasCollectionGaps;
        yield return new(
            "Process tracing coverage",
            complete
                ? trace.HasUnresolvedActivity
                    ? "Collection finished. Some system events could not be matched to a file; the counts are in the report. This does not mean the GIF failed."
                    : "File activity was collected for this test."
                : (trace.Reason ?? $"Tracing is {trace.State.ToLowerInvariant()}; some activity may be missing.")
                    + " Repeat the capture and retain the export if the gap recurs.",
            complete
                ? trace.HasUnresolvedActivity
                    ? "Info"
                    : "Success"
                : "Warning"
        );
        if (trace.StartedAt is null)
        {
            yield break;
        }

        var external = ActivityFiles(trace).Where(item => !item.File.IsApp).ToList();
        if (DiagnosticImageReadFinding.Create(session, external.Select(item => item.File).ToArray()) is { } reads)
        {
            yield return reads;
        }

        // Our own probes/repairs stay in the report. Apply and hash checks already
        // surface their actual outcome, so they are not external image-access failures.
        var warnings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var routine = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, retainedOnly) in external)
        {
            var countScope = retainedOnly ? "retained" : "whole test";
            var retained = DiagnosticActivityTiming.Operations(trace, file).ToArray();
            var retainedFallbacks = retained.LongCount(operation => operation.IsFastIoFallback);
            var failures = file.Failures;
            if (
                file.FastIoFallbacks is null
                && DiagnosticActivityTiming.HasCompleteRawCategory(trace, file, DiagnosticActivityKind.Failure, retained)
            )
            {
                failures -= retainedFallbacks;
            }

            if (failures > 0)
            {
                var timing = DiagnosticActivityTiming.Classify(session, file, DiagnosticActivityKind.Failure, !retainedOnly);
                var warning = IsIndependentApplication(file) && timing.Phase == DiagnosticActivityPhase.AfterVerification;
                var total = file.FastIoFallbacks is null
                    ? $"{file.Failures} unsuccessful operation(s), whole test; the legacy total may include Fast I/O fallback."
                    : $"{file.Failures} failed operation(s), {countScope}.";
                Add(warning ? warnings : routine, file.Path, $"{Identity(file)}: {total} {DescribeTiming(timing)}{AttributionNote(file)}");
            }

            if (file.Modifications > 0)
            {
                var timing = DiagnosticActivityTiming.Classify(session, file, DiagnosticActivityKind.Modification, !retainedOnly);
                Add(
                    routine,
                    file.Path,
                    $"{Identity(file)}: {file.Modifications} completed write, rename, or delete operation(s), {countScope}. "
                        + $"{DescribeTiming(timing)}{AttributionNote(file)} "
                        + "File operations alone do not establish changed image contents."
                        + TraceHashCorrelation.Describe(session, file.Path)
                );
            }

            if ((file.FastIoFallbacks ?? retainedFallbacks) > 0)
            {
                var total = file.FastIoFallbacks is { } count
                    ? $"{count} Fast I/O fallback result(s), {countScope}."
                    : $"{retainedFallbacks} retained Fast I/O fallback result(s); whole test fallback total is unavailable in this older report.";
                var timing = DiagnosticActivityTiming.Classify(session, file, DiagnosticActivityKind.FastIoFallback, !retainedOnly);
                Add(
                    routine,
                    file.Path,
                    $"{Identity(file)}: {total} STATUS_FLT_DISALLOW_FAST_IO (0xC01C0004) can select another I/O path; "
                        + $"it is neither a successful operation nor a terminal access failure. {DescribeTiming(timing)}{AttributionNote(file)}"
                );
            }
        }

        if (warnings.Count > 0)
        {
            yield return Group(
                "Image access failures",
                "An independently identified application made a failed file-access attempt after the intended GIF copy was verified. "
                    + "Later successful operations do not erase that attempt; it does not establish a playback failure. "
                    + "If visible behavior is unexplained, compare selected and reference GIF runs and describe the screen and symptom.",
                "Warning",
                warnings
            );
        }

        if (routine.Count > 0)
        {
            yield return Group(
                "Other file activity",
                "Setup activity, inconclusive attempts, I/O fallback and completed modifications provide context. "
                    + "Counts identify whole-test totals or retained records when an aggregate is unavailable. Expand for timing and process details.",
                "Info",
                routine
            );
        }
    }

    internal static string Identity(TraceAggregate file) =>
        $"{file.ProcessName} (PID {file.ProcessId}, instance {file.ProcessInstance}, session {file.SessionId?.ToString() ?? "unknown"})";

    private static IEnumerable<(TraceAggregate File, bool RetainedOnly)> ActivityFiles(ProcessTraceEvidence trace)
    {
        static (string Path, int ProcessId, long ProcessInstance) Key(string path, int processId, long processInstance) =>
            (path.ToUpperInvariant(), processId, processInstance);

        var aggregated = trace.Files.Select(file => Key(file.Path, file.ProcessId, file.ProcessInstance)).ToHashSet();
        foreach (var file in trace.Files)
        {
            yield return (file, false);
        }

        // Aggregate capacity and detail retention are independent. Retained positive
        // evidence remains usable even when no whole-test aggregate survived for its key.
        foreach (var group in trace.Operations.GroupBy(operation => Key(operation.Path, operation.ProcessId, operation.ProcessInstance)))
        {
            if (aggregated.Contains(group.Key))
            {
                continue;
            }

            var first = group.First();
            var file = new TraceAggregate
            {
                Path = first.Path,
                ProcessId = first.ProcessId,
                ProcessInstance = first.ProcessInstance,
                ProcessName = first.ProcessName,
                SessionId = first.SessionId,
                IsApp = group.Any(operation => operation.IsApp),
                AttributionResolved = group.All(operation =>
                    operation.AttributionResolved
                    && operation.ProcessName.Equals(first.ProcessName, StringComparison.OrdinalIgnoreCase)
                    && operation.SessionId == first.SessionId
                    && operation.IsApp == first.IsApp
                ),
                Reads = group.LongCount(operation => operation.Operation == "Read" && operation.Succeeded && operation.CompletedBytes > 0),
                Failures = group.LongCount(operation => operation.IsFailure),
                FastIoFallbacks = group.LongCount(operation => operation.IsFastIoFallback),
                Modifications = group.LongCount(operation => operation.Succeeded && operation.Operation is "Write" or "Rename" or "Delete"),
            };
            yield return (file, true);
        }
    }

    private static bool IsIndependentApplication(TraceAggregate file) => file.AttributionResolved && !file.IsApp && file.ProcessId > 4;

    private static string AttributionNote(TraceAggregate file) =>
        file.ProcessId == 4 ? " System I/O: initiating application unknown."
        : !file.AttributionResolved || file.ProcessId <= 4
            ? " Process attribution is unresolved; an independent application is not established."
        : "";

    private static string DescribeTiming(DiagnosticActivityClassification timing)
    {
        var phase = timing.Phase switch
        {
            DiagnosticActivityPhase.BeforeVerification =>
                "All observed operations in this category completed before this GIF copy was verified.",
            DiagnosticActivityPhase.AfterVerification => "An operation started after this GIF copy was verified.",
            DiagnosticActivityPhase.OverlapsVerification =>
                "An operation crossed verification; it cannot establish an attempt on the verified GIF generation.",
            _ => "Timing relative to verification is unknown: a verified boundary or complete operation timing is unavailable.",
        };
        return timing.Witness is { } witness
            ? $"{phase} {witness.Operation}, status 0x{witness.Status:X8}, started {witness.StartedAt:O}, completed {witness.CompletedAt:O}."
            : phase;
    }

    private static void Add(Dictionary<string, List<string>> groups, string path, string detail)
    {
        if (!groups.TryGetValue(path, out var details))
        {
            groups[path] = details = [];
        }

        details.Add(detail);
    }

    private static DiagnosticFinding Group(string title, string detail, string severity, Dictionary<string, List<string>> groups) =>
        new(title, detail, severity)
        {
            Children = groups.Select(group => new DiagnosticFinding(group.Key, string.Join(" ", group.Value), severity)).ToList(),
        };
}
