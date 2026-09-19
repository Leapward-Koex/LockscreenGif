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
    except ValueError:
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


def summarize(report):
    trace = report.get("ProcessTrace") or {}
    operations = trace.get("Operations") or []
    retained = {}
    for operation in operations:
        if successful_read(operation):
            retained.setdefault(identity_key(operation), []).append(operation)
    baseline = next((snapshot for snapshot in report.get("Snapshots", [])
                     if snapshot.get("Reason") == "Baseline"), {})
    old_files = {file["Path"].casefold(): file for file in baseline.get("Files", [])}
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
        "ApplySucceeded": (report.get("ApplyResult") or {}).get("Success"),
        "LockObserved": report.get("LockObserved"), "UnlockObserved": report.get("UnlockObserved"),
        "TraceState": trace.get("State"), "TraceReason": trace.get("Reason"),
        "Counters": {name: trace.get(name) for name in COUNTERS},
        "RetainedOperations": len(operations),
        "Timeline": [{"Utc": iso(timestamp(event.get("Timestamp"))), "Category": event.get("Category"),
                      "Message": event.get("Message")}
                     for event in report.get("Events", [])
                     if event.get("Category") in ("Session", "Windows session", "Snapshot")],
        "ReadsByAppliedFileAndProcess": rows,
        "Limitations": [
            "Whole-trace totals include baseline reads and previous file contents.",
            "Retained operations may be incomplete; positive reads survive loss, absence is inconclusive.",
            "System I/O can be caused by app inspection; timing does not identify its initiator.",
            "File access does not prove decoding or animation. Legacy boundaries are labeled in each row.",
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
