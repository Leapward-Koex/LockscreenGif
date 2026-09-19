import copy
import json
import tempfile
import unittest
import warnings
from pathlib import Path
from zipfile import ZipFile

from summarize_report import load_report, summarize

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

    def test_does_not_mutate_report(self):
        report = fixture()
        original = copy.deepcopy(report)
        summarize(report)
        self.assertEqual(report, original)


if __name__ == "__main__":
    unittest.main()
