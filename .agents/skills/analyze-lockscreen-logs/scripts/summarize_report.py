"""Read a diagnostic ZIP/session.json without extraction; output compact evidence, not a diagnosis."""
import argparse
import json
import math
from datetime import datetime, timezone
from pathlib import Path
from zipfile import BadZipFile, ZipFile

MAX_REPORT_BYTES = 128 * 1024 * 1024
COUNTERS = (
    "EventsLost", "QueueDropped", "UnmatchedOperations", "UnmatchedCompletions",
    "UnresolvedPaths", "UnresolvedProcesses", "OmittedOperations", "OmittedAggregates",
)
COLLECTION_GAP_COUNTERS = (
    "EventsLost", "QueueDropped", "UnmatchedOperations", "UnresolvedProcesses",
    "OmittedOperations", "OmittedAggregates",
)
FAST_IO_DISALLOWED = 0xC01C0004


def load_report(path):
    path = Path(path)
    if path.suffix.lower() == ".zip":
        with ZipFile(path) as archive:
            entries = [entry for entry in archive.infolist() if entry.filename == "session.json"]
            if len(entries) != 1:
                raise ValueError("Expected exactly one root session.json entry.")
            entry = entries[0]
            if entry.file_size > MAX_REPORT_BYTES:
                raise ValueError("session.json exceeds the 128 MiB analysis limit.")
            with archive.open(entry) as stream:
                data = stream.read(MAX_REPORT_BYTES + 1)
    else:
        with path.open("rb") as stream:
            data = stream.read(MAX_REPORT_BYTES + 1)
    if len(data) > MAX_REPORT_BYTES:
        raise ValueError("Report exceeds the 128 MiB analysis limit.")
    report = json.loads(data.decode("utf-8-sig"))
    if not isinstance(report, dict):
        raise ValueError("Expected a session JSON object.")
    return report


def timestamp(value):
    if not isinstance(value, str):
        return None
    try:
        result = datetime.fromisoformat(value.replace("Z", "+00:00"))
        return result.astimezone(timezone.utc) if result.tzinfo else None
    except (ValueError, OverflowError):
        return None


def iso(value):
    return value.isoformat().replace("+00:00", "Z") if value else None


def successful_read(operation):
    status = operation.get("Status")
    completed = operation.get("CompletedBytes")
    started = timestamp(operation.get("Timestamp"))
    ended = timestamp(operation.get("CompletedAt"))
    return (operation.get("Operation") == "Read" and isinstance(status, int)
            and status >= 0 and status & 0x80000000 == 0 and status != 0x103
            and isinstance(completed, (int, float)) and completed > 0
            and started is not None and ended is not None and ended >= started)


def identity_key(item):
    return (item.get("Path", "").casefold(), item.get("ProcessId"), item.get("ProcessInstance"))


def operation_succeeded(operation):
    status = operation.get("Status")
    return type(status) is int and 0 <= status <= 0xffffffff and status & 0x80000000 == 0 and status != 0x103


def fast_io_fallback(operation):
    return type(operation.get("Status")) is int and operation["Status"] == FAST_IO_DISALLOWED


def genuine_failure(operation):
    status = operation.get("Status")
    read_eof = (operation.get("Operation") == "Read" and status == 0xc0000011
                and operation.get("CompletedBytes") in (None, 0))
    return (type(status) is int and 0 <= status <= 0xffffffff and status & 0x80000000 != 0
            and not read_eof and not fast_io_fallback(operation))


def modification(operation):
    return operation.get("Operation") in ("Write", "Rename", "Delete") and operation_succeeded(operation)


def native_failure_witness(operation):
    # The collector has already excluded normal EOF using CompletedBytes, which
    # bounded witnesses do not carry. An abnormal positive-byte EOF can remain.
    status = operation.get("Status")
    return (type(status) is int and 0 <= status <= 0xffffffff and status & 0x80000000 != 0
            and not fast_io_fallback(operation))


def activity_timestamp(value):
    result = timestamp(value)
    return result if result != datetime.min.replace(tzinfo=timezone.utc) else None


def activity_witness(operation, predicate, raw=False):
    if not isinstance(operation, dict) or not predicate(operation):
        return None
    started = activity_timestamp(operation.get("Timestamp" if raw else "StartedAt"))
    completed = activity_timestamp(operation.get("CompletedAt"))
    if started is None or completed is None or completed < started:
        return None
    return {"StartedAt": started, "CompletedAt": completed,
            "Operation": operation.get("Operation"), "Status": operation.get("Status")}


def exported_witness(witness):
    return ({**witness, "StartedAt": iso(witness["StartedAt"]), "CompletedAt": iso(witness["CompletedAt"])}
            if witness else None)


def collection_complete(trace):
    return (trace.get("State") == "Completed" and not trace.get("HasCollectionGaps", False)
            and all(type(trace.get(name, 0)) is int and trace.get(name, 0) == 0
                    for name in COLLECTION_GAP_COUNTERS))


def exact_verification_boundary(file):
    # Activity warnings never use the legacy read-analysis boundary guesses.
    return (activity_timestamp(file.get("VerifiedAt"))
            if file.get("Copied") and file.get("Verified") and not file.get("Error") else None)


def activity_timing(aggregate, operations, boundary, complete, category):
    failure = category == "Failure"
    fallback = category == "FastIoFallback"
    predicate = genuine_failure if failure else fast_io_fallback if fallback else modification
    raw = [operation for operation in operations if predicate(operation)]
    raw_witnesses = [witness for operation in raw
                     if (witness := activity_witness(operation, predicate, raw=True))]
    timing = aggregate.get(category + "Timing")
    timing = timing if isinstance(timing, dict) else None
    native_predicate = native_failure_witness if failure else predicate
    latest_start = activity_witness((timing or {}).get("LatestStarted"), native_predicate)
    latest_complete = activity_witness((timing or {}).get("LatestCompleted"), native_predicate)
    witnesses = raw_witnesses + [item for item in (latest_start, latest_complete) if item]
    newest_start = max(witnesses, key=lambda item: item["StartedAt"], default=None)
    newest_complete = max(witnesses, key=lambda item: item["CompletedAt"], default=None)
    source = "Bounded aggregate and retained operations" if timing else "Retained operations only"
    count = aggregate.get("Failures" if failure else "FastIoFallbacks" if fallback else "Modifications")
    # BeforeVerification asserts every captured event in this category was earlier.
    # Legacy failure totals include fallback attempts, so reconcile those too.
    reconciliation = raw
    if (failure or fallback) and aggregate.get("FastIoFallbacks") is None:
        count = aggregate.get("Failures")
        reconciliation = [operation for operation in operations
                          if genuine_failure(operation) or fast_io_fallback(operation)]
    raw_complete = (type(count) is int and count > 0 and len(reconciliation) == count
                    and all(activity_witness(operation, lambda _: True, raw=True)
                            for operation in reconciliation))
    timed_summary_complete = (timing is not None and type(count) is int and count > 0
                              and latest_start is not None and latest_complete is not None
                              and type(timing.get("UntimedCount")) is int and timing["UntimedCount"] == 0)
    all_timed = timed_summary_complete if timing else raw_complete and complete
    phase = "Unknown"
    if boundary and newest_start and newest_start["StartedAt"] >= boundary:
        phase = "AfterVerification"
    elif boundary and any(item["StartedAt"] < boundary <= item["CompletedAt"] for item in witnesses):
        phase = "OverlapsVerification"
    elif (boundary and all_timed and newest_complete
          and newest_complete["CompletedAt"] < boundary):
        phase = "BeforeVerification"
    return {
        "Phase": phase, "TimingSource": source, "PhaseDescribesObservedActivityOnly": True,
        "LatestStartedWitness": exported_witness(newest_start),
        "LatestCompletedWitness": exported_witness(newest_complete),
        "RetainedCategoryOperations": len(raw),
        "RetainedCategoryOperationsWithValidTiming": len(raw_witnesses),
        "LegacyRawCountReconciled": raw_complete if timing is None else None,
        "UntimedAggregateOperations": (timing or {}).get("UntimedCount"),
    }


def activity_summary(report):
    trace = report.get("ProcessTrace") or {}
    complete = collection_complete(trace)
    applied = {}
    for file in (report.get("ApplyResult") or {}).get("Files", []):
        key = file.get("Path", "").casefold()
        boundary = exact_verification_boundary(file)
        if key not in applied or boundary and (applied[key] is None or boundary > applied[key]):
            applied[key] = boundary
    grouped = {}
    for operation in trace.get("Operations") or []:
        grouped.setdefault(identity_key(operation), []).append(operation)
    aggregates = {identity_key(item): item for item in trace.get("Files") or []}
    rows = []
    for key in dict.fromkeys([*aggregates, *grouped]):
        operations = grouped.get(key, [])
        aggregate = aggregates.get(key, {})
        if not (aggregate.get("Failures") or aggregate.get("Modifications") or aggregate.get("FastIoFallbacks")
                or any(genuine_failure(item) or modification(item) or fast_io_fallback(item) for item in operations)):
            continue
        identity = aggregate or operations[0]
        pid = identity.get("ProcessId")
        attribution = ("App/child" if identity.get("IsApp") else
                       "Unresolved" if not identity.get("AttributionResolved") else
                       "System I/O; initiator not established" if pid == 4 else
                       "External application" if type(pid) is int and pid > 4 else "Unresolved")
        boundary = applied.get(key[0])
        failure = activity_timing(aggregate, operations, boundary, complete, "Failure")
        changes = activity_timing(aggregate, operations, boundary, complete, "Modification")
        fallbacks = activity_timing(aggregate, operations, boundary, complete, "FastIoFallback")
        warning = attribution == "External application" and failure["Phase"] == "AfterVerification"
        rows.append({
            "Path": identity.get("Path"), "Process": identity.get("ProcessName"),
            "ProcessId": pid, "ProcessInstance": identity.get("ProcessInstance"), "Attribution": attribution,
            "IntendedPath": key[0] in applied, "VerificationBoundaryUtc": iso(boundary),
            "WholeTraceFailureCounter": aggregate.get("Failures"),
            "FailureCounterIncludesFastIoFallbacks": (
                aggregate.get("FastIoFallbacks") is None if aggregate.get("Failures") is not None else None),
            "WholeTraceFastIoFallbacks": aggregate.get("FastIoFallbacks"),
            "RetainedFastIoFallbacks": sum(fast_io_fallback(item) for item in operations),
            "WholeTraceModifications": aggregate.get("Modifications"),
            "Failure": failure, "Modification": changes, "FastIoFallback": fallbacks,
            "IndependentFailureAfterVerificationObserved": warning,
            "FailureSeverity": "Warning" if warning else "Info", "ModificationSeverity": "Info",
        })
    return rows


def verification_boundary(file, report):
    if not (file.get("Copied") and file.get("Verified") and not file.get("Error")):
        return None, "Copy not verified"
    if verified := timestamp(file.get("VerifiedAt")):
        return verified, "VerifiedAt"
    # Older schema-2 reports predate VerifiedAt. Label the fallback explicitly.
    expected = "The destination matches the source GIF. — " + file.get("Path", "")
    matches = [timestamp(event.get("Timestamp")) for event in report.get("Events", [])
               if event.get("Category") == "Apply: Verification"
               and event.get("Message", "").casefold() == expected.casefold()]
    matches = [item for item in matches if item]
    if matches:
        return max(matches), "Legacy verification event"
    after = [timestamp(snapshot.get("Timestamp")) for snapshot in report.get("Snapshots", [])
             if snapshot.get("Reason") == "AfterApply"]
    after = [item for item in after if item]
    return (min(after), "Conservative AfterApply boundary") if after else (None, "Unavailable")


def shutdown_summary(report):
    trace = report.get("ProcessTrace") or {}
    shutdown = trace.get("Shutdown") or {}
    current = shutdown.get("Current") or {}
    at_stop = shutdown.get("AtNativeStopReturn") or {}
    at_deadline = shutdown.get("AtDrainDeadline") or {}
    before, after = at_stop.get("CallbacksFinished"), at_deadline.get("CallbacksFinished")
    callback_delta = (after - before if type(before) is int and type(after) is int
                      and 0 <= before <= after else None)
    relevant_times = [timestamp(operation.get(field))
                      for operation in trace.get("Operations", [])
                      for field in ("Timestamp", "CompletedAt")]
    relevant_times += [timestamp(file.get("LastAt")) for file in trace.get("Files", [])]
    latest_relevant = max((at for at in relevant_times if at), default=None)
    final_hash_times = [timestamp(file.get("HashReadAt"))
                        for snapshot in report.get("Snapshots", [])
                        if snapshot.get("Reason") == "AfterUnlock"
                        for file in snapshot.get("Files", [])
                        if file.get("HashSource") == "Full read" and file.get("Stable") is True]
    latest_hash = max((at for at in final_hash_times if at), default=None)
    return {
        "Available": bool(shutdown),
        "WorkerStage": shutdown.get("WorkerStage"),
        "StageAtDeadline": shutdown.get("StageAtDeadline"),
        "NativeStopAttempted": shutdown.get("NativeStopAttempted"),
        "NativeStopStatus": shutdown.get("NativeStopStatus"),
        "NativeStopElapsedMilliseconds": shutdown.get("NativeStopElapsedMilliseconds"),
        "NativeSessionBuffersAtStop": shutdown.get("NativeStopBuffers"),
        "ConsumerCompletedNormally": shutdown.get("ConsumerCompletedNormally"),
        "TimesUtc": {key: iso(timestamp(shutdown.get(key))) for key in (
            "ConsumerStartedAt", "StopRequestedAt", "NativeStopStartedAt", "NativeStopReturnedAt",
            "DrainWaitStartedAt", "DrainDeadlineExceededAt", "ForceStopRequestedAt",
            "ForcedStopGraceExceededAt", "ConsumerReturnedAt", "CleanupStartedAt", "CleanupCompletedAt")},
        "CallbacksFinishedBetweenNativeStopAndDeadline": callback_delta,
        "CallbackInProgressAtDeadline": (
            at_deadline.get("CallbacksStarted") > at_deadline.get("CallbacksFinished")
            if type(at_deadline.get("CallbacksStarted")) is int
            and type(at_deadline.get("CallbacksFinished")) is int else None),
        "LatestConsumerEventUtc": iso(timestamp(current.get("LatestEventTimestamp"))),
        "LastConsumerCallbackFinishedUtc": iso(timestamp(current.get("LastCallbackFinishedAt"))),
        "LatestRelevantActivityUtc": iso(latest_relevant),
        "FinalFreshHashReadUtc": iso(latest_hash),
        "FinalHashMinusLatestRelevantSeconds": (
            (latest_hash - latest_relevant).total_seconds() if latest_hash and latest_relevant else None),
    }


def summarize(report):
    trace = report.get("ProcessTrace") or {}
    operations = trace.get("Operations") or []
    retained = {}
    for operation in operations:
        if successful_read(operation):
            retained.setdefault(identity_key(operation), []).append(operation)
    baseline = next((snapshot for snapshot in report.get("Snapshots", [])
                     if snapshot.get("Reason") == "Baseline"), {})
    baseline_files = baseline.get("Files", [])
    known_lengths = [file["Length"] for file in baseline_files
                     if type(file.get("Length")) is int and file["Length"] >= 0]
    old_files = {file["Path"].casefold(): file for file in baseline_files}
    applied = {file["Path"].casefold(): file for file in (report.get("ApplyResult") or {}).get("Files", [])}
    rows = []
    for aggregate in trace.get("Files", []):
        if not aggregate.get("Reads"):
            continue
        key = identity_key(aggregate)
        if key[0] not in applied:
            continue
        before = old_files.get(key[0], {})
        verified, boundary_source = verification_boundary(applied[key[0]], report)
        reads = retained.get(key, [])
        latest = max((timestamp(read["Timestamp"]) for read in reads), default=None)
        latest_source = "Retained operations only" if latest else "Unavailable"
        aggregate_start = timestamp(aggregate.get("LastReadStartedAt"))
        aggregate_end = timestamp(aggregate.get("LastReadCompletedAt"))
        if aggregate_start and aggregate_end and aggregate_end >= aggregate_start:
            if latest is None or aggregate_start >= latest:
                latest, latest_source = aggregate_start, "Aggregate successful-read timestamps"
        last_activity = timestamp(aggregate.get("LastAt"))
        process_id = aggregate.get("ProcessId")
        attribution = ("App/child" if aggregate.get("IsApp") else
                       "Unresolved" if not aggregate.get("AttributionResolved") else
                       "System I/O; initiator not established" if process_id == 4 else
                       "External application" if isinstance(process_id, int) and process_id > 4 else
                       "Unresolved")
        length = before.get("Length")
        rows.append({
            "Path": aggregate.get("Path"), "Process": aggregate.get("ProcessName"),
            "ProcessId": process_id, "ProcessInstance": aggregate.get("ProcessInstance"),
            "Attribution": attribution,
            "WholeTraceReads": aggregate.get("Reads"), "WholeTraceReadBytes": aggregate.get("ReadBytes"),
            "BaselineBytes": length,
            "Baseline64KiBChunks": math.ceil(length / 65536) if isinstance(length, int) else None,
            "WholeTraceBytesEqualBaseline": length is not None and aggregate.get("ReadBytes") == length,
            "VerificationBoundaryUtc": iso(verified), "BoundarySource": boundary_source,
            "AllActivityBeforeVerification": bool(last_activity and verified and last_activity < verified),
            "LatestSuccessfulReadStartUtc": iso(latest), "ReadTimingSource": latest_source,
            "RetainedSuccessfulReads": len(reads),
            "IndependentReadAfterVerificationObserved": bool(
                attribution == "External application" and latest and verified and latest >= verified),
        })
    return {
        "Session": report.get("Id"), "SchemaVersion": report.get("SchemaVersion"),
        "AppBuildId": (report.get("Environment") or {}).get("App build ID"),
        "StartedUtc": iso(timestamp(report.get("StartedAt"))),
        "EndedUtc": iso(timestamp(report.get("EndedAt"))),
        "SourceBytes": (report.get("Gif") or {}).get("SizeBytes"),
        "BaselineInventory": {
            "Complete": baseline.get("Complete"), "FileCount": len(baseline_files),
            "FilesWithKnownSize": len(known_lengths),
            "TotalKnownBytes": sum(known_lengths) if known_lengths else None,
            "LargestKnownFileBytes": max(known_lengths, default=None),
        },
        "ApplySucceeded": (report.get("ApplyResult") or {}).get("Success"),
        "LockObserved": report.get("LockObserved"), "UnlockObserved": report.get("UnlockObserved"),
        "TraceState": trace.get("State"), "TraceReason": trace.get("Reason"),
        "Counters": {name: trace.get(name) for name in COUNTERS},
        "RetainedOperations": len(operations),
        "ActivityCoverageComplete": collection_complete(trace),
        "TraceShutdown": shutdown_summary(report),
        "Timeline": [{"Utc": iso(timestamp(event.get("Timestamp"))), "Category": event.get("Category"),
                      "Message": event.get("Message")}
                     for event in report.get("Events", [])
                     if event.get("Category") in ("Session", "Windows session", "Snapshot")],
        "ReadsByAppliedFileAndProcess": rows,
        "ActivityByFileAndProcess": activity_summary(report),
        "Limitations": [
            "Whole-trace totals include baseline reads and previous file contents.",
            "Shutdown checkpoints describe consumer progress, not image reads; no progress alone cannot identify a stall.",
            "Worker cleanup time and zero loss counters do not prove complete event coverage.",
            "Retained operations may be incomplete; positive reads survive loss, absence is inconclusive.",
            "System I/O can be caused by app inspection; timing does not identify its initiator.",
            "File access does not prove decoding or animation. Legacy boundaries are labeled in each row.",
            "Activity phases use only successful per-file VerifiedAt, never guessed legacy boundaries.",
            "Activity counts cover the whole test; a later witness does not give an exact post-verification count.",
            "FAST_IO_DISALLOWED is a fallback request, not a genuine failure or a successful read.",
            "Aggregate phases describe observed activity even with gaps; legacy all-before reconstruction needs complete coverage.",
            "Modification activity is informational; changed bytes require separate fresh hash evidence.",
        ],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", help="Diagnostic ZIP or session.json; no files are extracted.")
    args = parser.parse_args()
    try:
        print(json.dumps(summarize(load_report(args.report)), indent=2, ensure_ascii=True))
    except (OSError, ValueError, KeyError, BadZipFile) as error:
        parser.exit(1, f"Cannot analyze report: {error}\n")


if __name__ == "__main__":
    main()
