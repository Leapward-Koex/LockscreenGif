import copy
import json
import tempfile
import unittest
import warnings
from pathlib import Path
from zipfile import ZipFile

from summarize_report import FAST_IO_DISALLOWED, genuine_failure, load_report, summarize

PATH = "<cache>/LockScreen_A/LockScreen.jpg"
OLD_SIZE = 145966787


def fixture():
    return {
        "SchemaVersion": 2, "Id": "synthetic",
        "Gif": {"SizeBytes": 43495},
        "ApplyResult": {"Success": True, "Files": [
            {"Path": PATH, "Copied": True, "Verified": True, "VerifiedAt": "2026-09-19T16:14:06+12:00"}]},
        "Snapshots": [{"Reason": "Baseline", "Files": [{"Path": PATH, "Length": OLD_SIZE}]}],
        "ProcessTrace": {
            "State": "Incomplete", "QueueDropped": 41534,
            "Files": [{"Path": PATH, "ProcessId": 4, "ProcessInstance": 1, "ProcessName": "System",
                       "AttributionResolved": True, "Reads": 2228, "ReadBytes": OLD_SIZE,
                       "LastAt": "2026-09-19T04:14:05Z"}],
            "Operations": []},
    }


def activity_fixture():
    report = fixture()
    report["ProcessTrace"].update(State="Completed", QueueDropped=0)
    report["ProcessTrace"]["Files"][0].update(
        ProcessId=77, ProcessName="Reader.exe", Failures=1, Modifications=0)
    return report


def operation(report, started="2026-09-19T04:14:07Z", completed="2026-09-19T04:14:08Z",
              name="Read", status=0xc0000022):
    aggregate = report["ProcessTrace"]["Files"][0]
    return {key: aggregate[key] for key in ("Path", "ProcessId", "ProcessInstance", "ProcessName", "AttributionResolved")} | {
        "Timestamp": started, "CompletedAt": completed, "Operation": name, "Status": status}


def timing(started="2026-09-19T04:14:07Z", completed="2026-09-19T04:14:08Z",
           name="Read", status=0xc0000022):
    witness = {"StartedAt": started, "CompletedAt": completed, "Operation": name, "Status": status}
    return {"LatestStarted": witness, "LatestCompleted": copy.deepcopy(witness), "UntimedCount": 0}


def activity_row(report):
    return summarize(report)["ActivityByFileAndProcess"][0]


class ReportTests(unittest.TestCase):
    def test_large_baseline_is_not_applied_gif_access(self):
        report = fixture()
        result = summarize(report)
        row = result["ReadsByAppliedFileAndProcess"][0]
        self.assertEqual(row["Baseline64KiBChunks"], 2228)
        self.assertTrue(row["WholeTraceBytesEqualBaseline"])
        self.assertTrue(row["AllActivityBeforeVerification"])
        self.assertFalse(row["IndependentReadAfterVerificationObserved"])
        self.assertEqual(result["Counters"]["QueueDropped"], 41534)

    def test_completed_application_read_qualifies(self):
        report = fixture()
        aggregate = report["ProcessTrace"]["Files"][0]
        aggregate.update(ProcessId=77, ProcessName="Reader.exe", Reads=2, ReadBytes=43495,
                         LastAt="2026-09-19T04:14:10Z")
        report["ProcessTrace"]["Operations"] = [
            dict(aggregate, Timestamp="2026-09-19T04:14:09Z", CompletedAt="2026-09-19T04:14:10Z",
                 Operation="Read", Status=0, CompletedBytes=43495)]
        row = summarize(report)["ReadsByAppliedFileAndProcess"][0]
        self.assertTrue(row["IndependentReadAfterVerificationObserved"])
        self.assertEqual(row["RetainedSuccessfulReads"], 1)
        self.assertEqual(row["VerificationBoundaryUtc"], "2026-09-19T04:14:06Z")

    def test_old_inflight_failed_or_app_reads_do_not_qualify(self):
        report = fixture()
        aggregate = report["ProcessTrace"]["Files"][0]
        aggregate.update(ProcessId=77, ProcessName="Reader.exe", LastAt="2026-09-19T04:14:12Z")
        base = dict(aggregate, Timestamp="2026-09-19T04:14:05Z", CompletedAt="2026-09-19T04:14:10Z",
                    Operation="Read", Status=0, CompletedBytes=32)
        for operation in [base, dict(base, Timestamp="2026-09-19T04:14:09Z", Status=0xc0000022),
                          dict(base, Timestamp="2026-09-19T04:14:09Z", Status=None)]:
            report["ProcessTrace"]["Operations"] = [operation]
            self.assertFalse(summarize(report)["ReadsByAppliedFileAndProcess"][0]
                             ["IndependentReadAfterVerificationObserved"])
        aggregate.update(IsApp=True, LastReadStartedAt="2026-09-19T04:14:09Z",
                         LastReadCompletedAt="2026-09-19T04:14:10Z")
        self.assertFalse(summarize(report)["ReadsByAppliedFileAndProcess"][0]
                         ["IndependentReadAfterVerificationObserved"])

    def test_aggregates_can_prove_a_later_read_after_detail_loss(self):
        report = fixture()
        report["ProcessTrace"]["Files"][0].update(
            ProcessId=77, LastReadStartedAt="2026-09-19T04:14:09Z",
            LastReadCompletedAt="2026-09-19T04:14:10Z")
        row = summarize(report)["ReadsByAppliedFileAndProcess"][0]
        self.assertTrue(row["IndependentReadAfterVerificationObserved"])
        self.assertEqual(row["RetainedSuccessfulReads"], 0)

    def test_legacy_boundary_is_explicit_and_unknown_is_not_guessed(self):
        report = fixture()
        del report["ApplyResult"]["Files"][0]["VerifiedAt"]
        row = summarize(report)["ReadsByAppliedFileAndProcess"][0]
        self.assertIsNone(row["VerificationBoundaryUtc"])
        report["Events"] = [{"Category": "Apply: Verification", "Timestamp": "2026-09-19T04:14:06Z",
                             "Message": "The destination matches the source GIF. — " + PATH}]
        row = summarize(report)["ReadsByAppliedFileAndProcess"][0]
        self.assertEqual(row["BoundarySource"], "Legacy verification event")

    def test_zip_is_read_without_extracting_and_duplicates_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "report.zip"
            with ZipFile(path, "w") as archive:
                archive.writestr("session.json", json.dumps(fixture()))
                archive.writestr("../do-not-extract", "untrusted content")
            self.assertEqual(load_report(path)["Id"], "synthetic")
            self.assertEqual(list(Path(temp).iterdir()), [path])
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", UserWarning)
                with ZipFile(path, "a") as archive:
                    archive.writestr("session.json", "{}")
            with self.assertRaises(ValueError):
                load_report(path)

    def test_baseline_workload_is_reported_separately_from_selected_source(self):
        report = fixture()
        report["Snapshots"][0].update(Complete=False)
        report["Snapshots"][0]["Files"].append({"Path": "<cache>/unknown.jpg"})
        result = summarize(report)
        self.assertEqual(result["SourceBytes"], 43495)
        self.assertEqual(result["BaselineInventory"], {
            "Complete": False, "FileCount": 2, "FilesWithKnownSize": 1,
            "TotalKnownBytes": OLD_SIZE, "LargestKnownFileBytes": OLD_SIZE})
        report["Snapshots"] = []
        self.assertIsNone(summarize(report)["BaselineInventory"]["LargestKnownFileBytes"])

    def test_shutdown_progress_and_final_hash_boundary_are_separate(self):
        report = fixture()
        report["ProcessTrace"]["Shutdown"] = {
            "StopRequestedAt": "2026-09-19T16:14:06+12:00",
            "NativeStopStatus": 0,
            "StageAtDeadline": "Processing", "WorkerStage": "Finished",
            "AtNativeStopReturn": {"CallbacksStarted": 10, "CallbacksFinished": 10},
            "AtDrainDeadline": {"CallbacksStarted": 31, "CallbacksFinished": 30},
            "Current": {"LatestEventTimestamp": "2026-09-19T04:14:09Z",
                        "LastCallbackFinishedAt": "2026-09-19T04:14:14Z"},
        }
        report["Snapshots"].append({"Reason": "AfterUnlock", "Files": [
            {"HashReadAt": "2026-09-19T04:14:07Z", "HashSource": "Full read", "Stable": True},
            {"HashReadAt": "2026-09-19T04:14:20Z", "HashSource": "Reused", "Stable": True}]})
        result = summarize(report)["TraceShutdown"]
        self.assertTrue(result["Available"])
        self.assertEqual(result["CallbacksFinishedBetweenNativeStopAndDeadline"], 20)
        self.assertTrue(result["CallbackInProgressAtDeadline"])
        self.assertEqual(result["TimesUtc"]["StopRequestedAt"], "2026-09-19T04:14:06Z")
        self.assertIsNone(result["TimesUtc"]["CleanupCompletedAt"])
        self.assertEqual(result["LatestConsumerEventUtc"], "2026-09-19T04:14:09Z")
        self.assertEqual(result["LatestRelevantActivityUtc"], "2026-09-19T04:14:05Z")
        self.assertEqual(result["FinalHashMinusLatestRelevantSeconds"], 2)

    def test_old_shutdown_measurements_are_unknown_not_zero(self):
        result = summarize(fixture())["TraceShutdown"]
        self.assertFalse(result["Available"])
        self.assertIsNone(result["NativeStopStatus"])
        self.assertIsNone(result["CallbacksFinishedBetweenNativeStopAndDeadline"])
        self.assertIsNone(result["CallbackInProgressAtDeadline"])

    def test_shutdown_invalid_counters_or_timestamps_are_not_guessed(self):
        report = fixture()
        report["ProcessTrace"]["Shutdown"] = {
            "StopRequestedAt": "2026-09-19T04:14:00",
            "AtNativeStopReturn": {"CallbacksFinished": 10},
            "AtDrainDeadline": {"CallbacksStarted": None, "CallbacksFinished": 5},
        }
        result = summarize(report)["TraceShutdown"]
        self.assertIsNone(result["TimesUtc"]["StopRequestedAt"])
        self.assertIsNone(result["CallbacksFinishedBetweenNativeStopAndDeadline"])
        self.assertIsNone(result["CallbackInProgressAtDeadline"])

    def test_does_not_mutate_report(self):
        report = fixture()
        original = copy.deepcopy(report)
        summarize(report)
        self.assertEqual(report, original)


class ActivityTests(unittest.TestCase):
    def test_real_failure_after_verification_survives_detail_loss(self):
        report = activity_fixture()
        report["ProcessTrace"].update(State="Incomplete", QueueDropped=20, OmittedOperations=100)
        report["ProcessTrace"]["Files"][0].update(FastIoFallbacks=0, FailureTiming=timing())
        result = summarize(report)
        row = result["ActivityByFileAndProcess"][0]
        self.assertEqual(row["Failure"]["Phase"], "AfterVerification")
        self.assertEqual(row["FailureSeverity"], "Warning")
        self.assertFalse(result["ActivityCoverageComplete"])
        self.assertEqual(row["Failure"]["RetainedCategoryOperations"], 0)

    def test_preparation_failure_uses_completion_witness_not_last_activity(self):
        report = activity_fixture()
        aggregate = report["ProcessTrace"]["Files"][0]
        aggregate.update(FastIoFallbacks=0, LastAt="2026-09-19T04:14:20Z",
                         FailureTiming=timing("2026-09-19T04:14:01Z", "2026-09-19T04:14:02Z"))
        row = activity_row(report)
        self.assertEqual(row["Failure"]["Phase"], "BeforeVerification")
        self.assertEqual(row["FailureSeverity"], "Info")

    def test_two_witnesses_preserve_slow_earlier_operation_crossing_boundary(self):
        report = activity_fixture()
        summary = timing("2026-09-19T04:14:04Z", "2026-09-19T04:14:05Z")
        summary["LatestCompleted"].update(StartedAt="2026-09-19T04:14:01Z", CompletedAt="2026-09-19T04:14:07Z")
        report["ProcessTrace"]["Files"][0].update(FastIoFallbacks=0, Failures=2, FailureTiming=summary)
        self.assertEqual(activity_row(report)["Failure"]["Phase"], "OverlapsVerification")
        self.assertEqual(activity_row(report)["FailureSeverity"], "Info")

    def test_aggregate_before_phase_survives_gaps_but_not_untimed_events(self):
        report = activity_fixture()
        summary = timing("2026-09-19T04:14:01Z", "2026-09-19T04:14:02Z")
        report["ProcessTrace"]["Files"][0].update(FastIoFallbacks=0, FailureTiming=summary)
        for field in ("QueueDropped", "OmittedOperations", "OmittedAggregates", "EventsLost",
                      "UnmatchedOperations", "UnresolvedProcesses"):
            changed = copy.deepcopy(report)
            changed["ProcessTrace"][field] = 1
            self.assertEqual(activity_row(changed)["Failure"]["Phase"], "BeforeVerification", field)
            self.assertFalse(summarize(changed)["ActivityCoverageComplete"])
        report["ProcessTrace"]["Files"][0]["FailureTiming"]["UntimedCount"] = 1
        self.assertEqual(activity_row(report)["Failure"]["Phase"], "Unknown")

    def test_legacy_fallback_count_reconciliation_does_not_create_failure(self):
        report = activity_fixture()
        report["ProcessTrace"]["Files"][0]["Failures"] = 2
        report["ProcessTrace"]["Operations"] = [
            operation(report, "2026-09-19T04:14:01Z", "2026-09-19T04:14:02Z"),
            operation(report, status=FAST_IO_DISALLOWED)]
        row = activity_row(report)
        self.assertEqual(row["Failure"]["Phase"], "BeforeVerification")
        self.assertTrue(row["Failure"]["LegacyRawCountReconciled"])
        self.assertTrue(row["FailureCounterIncludesFastIoFallbacks"])
        self.assertIsNone(row["WholeTraceFastIoFallbacks"])
        self.assertEqual(row["RetainedFastIoFallbacks"], 1)
        self.assertEqual(row["FastIoFallback"]["Phase"], "AfterVerification")
        self.assertEqual(row["FailureSeverity"], "Info")
        report["ProcessTrace"]["Operations"][0] = operation(report)
        self.assertEqual(activity_row(report)["FailureSeverity"], "Warning")

    def test_legacy_all_before_needs_exact_counts_and_complete_timing(self):
        report = activity_fixture()
        report["ProcessTrace"]["Operations"] = [
            operation(report, "2026-09-19T04:14:01Z", "2026-09-19T04:14:02Z")]
        self.assertEqual(activity_row(report)["Failure"]["Phase"], "BeforeVerification")
        missing = copy.deepcopy(report)
        missing["ProcessTrace"]["Files"][0]["Failures"] = 2
        self.assertEqual(activity_row(missing)["Failure"]["Phase"], "Unknown")
        missing = copy.deepcopy(report)
        missing["ProcessTrace"]["Operations"][0]["CompletedAt"] = None
        self.assertEqual(activity_row(missing)["Failure"]["Phase"], "Unknown")
        report["ProcessTrace"]["State"] = "Incomplete"
        self.assertEqual(activity_row(report)["Failure"]["Phase"], "Unknown")
        report["ProcessTrace"]["State"] = "Completed"
        for field in ("QueueDropped", "OmittedOperations", "OmittedAggregates", "EventsLost",
                      "UnmatchedOperations", "UnresolvedProcesses"):
            changed = copy.deepcopy(report)
            changed["ProcessTrace"][field] = 1
            self.assertEqual(activity_row(changed)["Failure"]["Phase"], "Unknown", field)

    def test_legacy_verification_guess_is_not_used_for_activity_warnings(self):
        report = activity_fixture()
        del report["ApplyResult"]["Files"][0]["VerifiedAt"]
        report["Events"] = [{"Category": "Apply: Verification", "Timestamp": "2026-09-19T04:14:06Z",
                             "Message": "The destination matches the source GIF. — " + PATH}]
        report["ProcessTrace"]["Operations"] = [operation(report)]
        row = activity_row(report)
        self.assertIsNone(row["VerificationBoundaryUtc"])
        self.assertEqual(row["Failure"]["Phase"], "Unknown")
        self.assertEqual(row["FailureSeverity"], "Info")

    def test_app_system_and_unresolved_activity_remain_informational(self):
        for values in ({"IsApp": True}, {"ProcessId": 4}, {"AttributionResolved": False}):
            report = activity_fixture()
            report["ProcessTrace"]["Files"][0].update(values, FastIoFallbacks=0, FailureTiming=timing())
            row = activity_row(report)
            self.assertEqual(row["Failure"]["Phase"], "AfterVerification")
            self.assertEqual(row["FailureSeverity"], "Info")

    def test_modification_is_informational_even_after_verification(self):
        report = activity_fixture()
        report["ProcessTrace"]["Files"][0].update(Failures=0, FastIoFallbacks=0, Modifications=1,
                                                ModificationTiming=timing(name="Write", status=0))
        row = activity_row(report)
        self.assertEqual(row["Modification"]["Phase"], "AfterVerification")
        self.assertEqual(row["ModificationSeverity"], "Info")
        self.assertEqual(row["FailureSeverity"], "Info")

    def test_fallback_and_read_eof_are_neither_failure_nor_successful_read(self):
        report = activity_fixture()
        report["ProcessTrace"]["Operations"] = [operation(report, status=FAST_IO_DISALLOWED),
                                                operation(report, status=0xc0000011)]
        row = activity_row(report)
        self.assertEqual(row["Failure"]["RetainedCategoryOperations"], 0)
        self.assertEqual(row["FailureSeverity"], "Info")
        self.assertFalse(summarize(report)["ReadsByAppliedFileAndProcess"][0]
                         ["IndependentReadAfterVerificationObserved"])
        self.assertTrue(genuine_failure(operation(report, name="Write", status=0xc0000011)))

    def test_raw_positive_failure_survives_missing_aggregate(self):
        report = activity_fixture()
        report["ProcessTrace"]["Operations"] = [operation(report)]
        report["ProcessTrace"]["Files"] = []
        report["ProcessTrace"]["OmittedAggregates"] = 1
        row = activity_row(report)
        self.assertEqual(row["FailureSeverity"], "Warning")
        self.assertIsNone(row["WholeTraceFailureCounter"])

    def test_operation_on_unintended_path_cannot_warn(self):
        report = activity_fixture()
        report["ProcessTrace"]["Files"][0].update(Path="<cache>/other.jpg", FastIoFallbacks=0, FailureTiming=timing())
        row = activity_row(report)
        self.assertFalse(row["IntendedPath"])
        self.assertEqual(row["FailureSeverity"], "Info")

    def test_new_failure_counter_reconciles_without_fallback_attempts(self):
        report = activity_fixture()
        report["ProcessTrace"]["Files"][0].update(FastIoFallbacks=1)
        report["ProcessTrace"]["Operations"] = [
            operation(report, "2026-09-19T04:14:01Z", "2026-09-19T04:14:02Z"),
            operation(report, status=FAST_IO_DISALLOWED)]
        row = activity_row(report)
        self.assertEqual(row["Failure"]["Phase"], "BeforeVerification")
        self.assertFalse(row["FailureCounterIncludesFastIoFallbacks"])
        self.assertEqual(row["WholeTraceFailureCounter"], 1)
        self.assertEqual(row["WholeTraceFastIoFallbacks"], 1)

    def test_path_case_matches_but_process_lifetimes_do_not(self):
        report = activity_fixture()
        before = operation(report, "2026-09-19T04:14:01Z", "2026-09-19T04:14:02Z")
        before["Path"] = PATH.upper()
        report["ProcessTrace"]["Operations"] = [before]
        self.assertEqual(activity_row(report)["Failure"]["Phase"], "BeforeVerification")
        before["ProcessInstance"] = 99
        self.assertEqual(activity_row(report)["Failure"]["Phase"], "Unknown")

    def test_unscoped_uncertainty_does_not_prevent_legacy_before_classification(self):
        report = activity_fixture()
        report["ProcessTrace"].update(UnmatchedCompletions=40, UnresolvedPaths=100)
        report["ProcessTrace"]["Operations"] = [
            operation(report, "2026-09-19T04:14:01Z", "2026-09-19T04:14:02Z")]
        self.assertTrue(summarize(report)["ActivityCoverageComplete"])
        self.assertEqual(activity_row(report)["Failure"]["Phase"], "BeforeVerification")

    def test_invalid_or_pending_modification_witness_does_not_establish_activity(self):
        for start, end, status in (("2026-09-19T04:14:09Z", "2026-09-19T04:14:08Z", 0),
                                   ("2026-09-19T04:14:07Z", None, 0),
                                   ("2026-09-19T04:14:07Z", "2026-09-19T04:14:08Z", 0x103)):
            report = activity_fixture()
            report["ProcessTrace"]["Files"][0].update(Failures=0, FastIoFallbacks=0, Modifications=1,
                                                    ModificationTiming=timing(start, end, "Write", status))
            self.assertEqual(activity_row(report)["Modification"]["Phase"], "Unknown")

    def test_new_timing_summary_is_not_mutated(self):
        report = activity_fixture()
        report["ProcessTrace"]["Files"][0].update(FastIoFallbacks=0, FailureTiming=timing())
        original = copy.deepcopy(report)
        summarize(report)
        self.assertEqual(report, original)

    def test_default_dotnet_timestamps_are_unknown(self):
        report = activity_fixture()
        report["ProcessTrace"]["Files"][0].update(FastIoFallbacks=0, FailureTiming=timing())
        report["ApplyResult"]["Files"][0]["VerifiedAt"] = "0001-01-01T00:00:00+00:00"
        self.assertEqual(activity_row(report)["Failure"]["Phase"], "Unknown")
        report["ApplyResult"]["Files"][0]["VerifiedAt"] = "2026-09-19T04:14:06Z"
        report["ProcessTrace"]["Files"][0]["FailureTiming"] = timing(
            "0001-01-01T00:00:00Z", "2026-09-19T04:14:07Z")
        self.assertEqual(activity_row(report)["Failure"]["Phase"], "Unknown")

    def test_native_failure_summary_already_excludes_normal_eof(self):
        report = activity_fixture()
        report["ProcessTrace"]["Files"][0].update(FastIoFallbacks=0, FailureTiming=timing(status=0xc0000011))
        self.assertEqual(activity_row(report)["FailureSeverity"], "Warning")


if __name__ == "__main__":
    unittest.main()
