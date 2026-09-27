# Diagnostics

Open **Diagnostics**, select the current GIF or the reference animation, then choose **Start test**. After applying finishes, a dialog counts down five seconds before locking automatically. Choose **Lock now** to skip the countdown or **Cancel** to stay unlocked and continue manually. Check whether the GIF animates, then unlock whenever you're ready. Review the findings and use the fixed Export report button at the bottom of the page to save the ZIP report.

Setup shows the source and optional Windows step, followed by the Start test button. Normal readiness states are not displayed. Before the first test, unavailable monitoring or cache inspection produces a plain-language warning with a Check again action; source-selection guidance stays beside its controls. During and after a test, progress and Findings describe what happened instead of repeating preparation statuses.

The experimental **Windows API step** toggle defaults to off. It shares one saved preference with **Enable Windows lockscreen API** on Settings, persists between restarts, and also controls normal lockscreen applies. When enabled, it awaits `LockScreen.SetImageFileAsync` before cache discovery and replacement. If a GIF is not working, enabling this option and starting another test may help. Each new test records the selected toggle setting; changing the preference affects future applies, not a test already in progress.

Findings use a check mark for verified results, a warning for concrete problems or incomplete collection, and a neutral information icon for inconclusive evidence. A completed external read of any verified GIF copy passes the overall image-access check; other cached copies do not all need to be read. Missing reads and optional variants remain informational. Expand a file-check group to see each image, its individual status, and expected versus observed hashes. The report contains the same detailed findings.

## What the evidence means

- A verified copy means the destination bytes matched the selected source at the time of reading.
- Lock/unlock notifications establish that the session passed through the requested cycle.
- Fresh reads after unlocking can establish that the GIF remained in the applied files.
- Completed ETW reads establish file access by the identified process, not decoding or visible animation. App/helper inspection reads are excluded from external-read findings.
- No completed external read observed is inconclusive. Windows may reuse an in-memory image, and tracing can have gaps.
- Completed external writes, renames and deletes identify observed activity. Hash changes are associated with operations in the same interval; timing alone does not prove which operation produced the final bytes or which image Windows displayed.
- File checks cannot confirm visible animation. Structural GIF inspection does not fully decode or validate compressed pixel data.
- Unavailable registration, inaccessible inventories, unstable reads, collector limits, disconnects, suspension, cancellation, and interruptions remain visible as incomplete evidence.

Every valid diagnostic test automatically requests one elevated helper before the baseline and apply. The app stays unelevated. That connection is shared by ETW tracing and any cache permission repairs for the whole test; declining elevation or losing the helper never prompts again in that test. Basic monitoring and applying with existing access continue. If applying needs unavailable access, its failure is reported. Ordinary Apply/Remove operations still launch a short-lived helper only when a permission check actually fails.

The helper authenticates the initiating process, executable location and user SID; the app verifies the helper's pipe PID. Requests use bounded, versioned messages and monotonic request IDs. Repairs retain the previous per-path rules, read/Modify rights, reparse-point rejection and nonrecursive ownership fallback. The cache scope is recomputed from the initiating SID, including when different administrator credentials are supplied.

A dedicated, fixed-name, fixed-GUID real-time system logger captures only Process, Thread, DiskFileIO, FileIO and FileIOInit keywords. It does not enable stacks, sampling, registry or network events, and never configures an ETL output file. The helper normalizes device paths and discards unrelated file-operation details before transport. Process/thread generations, file mappings and IRPs correlate starts with completions; unresolved attribution stays explicit. No command lines, process paths, file contents or raw payloads are logged.

Collection has a 32 MiB ETW buffer ceiling, 4,096 queued operations, 10,000 retained operations or 16 MiB of operation data, and 512 per-file/process aggregates. Aggregates continue after operation retention fills. ETW loss, transport loss, unresolved identities/paths, unmatched starts/completions and retention omissions are recorded. Unknown system-wide paths and unmatched completions are retained as unscoped activity, not counted as confirmed loss on a relevant image. They produce an informational coverage finding after normal collection completes. Actual event loss, missing relevant completions, unknown relevant process identities, truncation and interrupted collection still warn. Normal read EOF is retained in raw operations but is not an access failure or a successful data read. The app's own probes and permission repairs remain in the report; their outcomes are assessed by apply/hash checks rather than external-access warnings. A 256 MiB helper private-memory limit stops tracing while basic checks continue.

The existing five-minute diagnostic deadline remains. After unlocking and the final observation interval, tracing stops and drains for up to five seconds. Cancellation, failure, timeout and closing share asynchronous cleanup outside the recorder lock. The helper watches its parent and connection and has a six-minute watchdog, also bounding native permission tools. An ownership mutex prevents simultaneous collectors; orphan cleanup verifies the fixed session identity and real-time/no-file configuration before stopping it. Unrelated tracing sessions are never stopped.

## Code boundaries

| Area | Responsibility |
| --- | --- |
| `Views/DiagnosticsPage.*`, `ViewModels/DiagnosticsViewModel.cs` | Native WinUI presentation, commands, findings, and export |
| `Services/Diagnostics/DiagnosticsSessionService.cs` | Application-owned current-test coordination and page-independent lifetime |
| `DiagnosticRun.cs`, `DiagnosticRecorder.cs` | One test's lifecycle and synchronized in-memory evidence |
| `WindowsSessionMonitor.cs` | Current-session WTS, display, and power notifications |
| `CacheCollector.cs`, `CacheFileReader.cs` | Watcher hints, bounded reconciliation, stable hashes, and read provenance |
| `EnvironmentCollector.cs`, `GifInspector.cs`, `ReferenceAnimation.cs` | Read-only environment evidence and source inspection |
| `DiagnosticAnalyzer.cs`, `DiagnosticReportWriter.cs`, `DiagnosticRedactor.cs` | Cautious findings and sanitized reports |
| `DiagnosticProcessTrace.cs`, `DiagnosticTraceFindings.cs`, `TraceHashCorrelation.cs` | Background trace polling, bounded current-test evidence, per-image findings and cautious hash association |
| `LockscreenGif.Privileged.Contracts/` | Shared evidence models, permission/session interfaces and bounded IPC protocol |
| `LockscreenGif.Privileged.Helper/` | Parent authentication, scoped cache repairs and helper lifecycle |
| `LockscreenGif.Privileged.Helper/Tracing/` | Session ownership, real-time ETW, identity/path mapping, correlation and bounded transport |
| `Services/Lockscreen/` | Authenticated helper client, cache discovery, atomic replacement, verification, and removal |
| `Models/Diagnostics/`, `Models/LockscreenApplyResult.cs` | Serializable evidence and structured apply results |

## Storage and export

Only the current test is held in memory. Starting a new test replaces it; closing the app discards it. Reports are written only when the user explicitly exports one. Selected source GIFs are read directly; the reference animation uses a temporary working file removed after applying. No diagnostic history or source copies are retained.

The activity timeline and technical details are collected for export rather than shown on the page. An export contains `summary.md`, `session.json`, and `events.jsonl`. Report schema 2 includes `ProcessTrace` collection times, status/reason, counters, operations and per-file/process aggregates. The page receives status/findings snapshots without serializing raw operation lists. Source/cache/rename paths and any path-valued trace fields pass through the same recursive redaction as the rest of the report. Source media and screenshots are excluded. A five-minute collection deadline and bounded event, inventory, and snapshot retention prevent indefinite monitoring; any evidence discarded by a limit is flagged. Inspect the report before publishing it.

Diagnostics exports also include redacted application `app_*.log` files under
`logs/`, using the same report-scoped aliases as the structured evidence. Log
snapshots are collected in the background at export time and can include activity
outside the current test. The eight most recently modified logs are considered,
with at most the last 2 MiB of each. Only complete lines are retained at snapshot
boundaries. `logs/manifest.json` records collection time, missing/unreadable files,
omitted older files, truncation, and dropped partial lines. Nonstandard log names
use generic ZIP entry names. An unavailable log directory or individual file does
not prevent exporting the rest of the report; output/ZIP write errors still fail
the export. Settings files, crash dumps, linked files, and subdirectories are not
included. **Settings > Logs > Save logs as ZIP** remains the separate export of
the original application logs without report redaction or these snapshot limits.

## Trace shutdown evidence

The optional `ProcessTrace.Shutdown` section adds bounded measurements to schema 2: stop request, native stop result/duration, consumer return, disposal/correlation completion, and progress snapshots at stop, native return and deadline. A dispatch hook counts callbacks and records event/delivery timestamps without retaining unrelated payloads. Native STOP buffer statistics are labeled separately from consumer progress. Missing fields in older reports remain unknown.

The five-second consumer drain limit is unchanged. A timeout warns that final file-activity events may be missing; it does not imply a failed GIF. The deadline's worker stage and progress remain in the report even after cleanup completes. If the subsequent two-second forced-stop grace period also expires, the helper permits retrieval of the partial report with that fact recorded. The summary calls start/end the collector lifetime, because cleanup time is not proof of event coverage.

`TraceProgressRecorder` owns the fixed-size measurements; `EtwFileSubscriptions` owns event subscriptions; `TraceWorkerDrain` owns the bounded wait; `EtwFileCollector.Shutdown` coordinates stopping. `DiagnosticTraceShutdownSummary` formats report details. None of these measurements creates a history or requests another elevation.

## Read timing and large baseline files

Each successful destination readback now records `ApplyResult.Files[].VerifiedAt`. The helper retains `LastReadStartedAt` and `LastReadCompletedAt` for the latest-started successful positive-byte read in each aggregate, even when a raw operation is dropped. Only independent application reads starting after verification can pass the applied-GIF access finding. System I/O stays in the export but can include the app's own inspection. Whole-collection read totals are no longer presented as counts of the applied GIF being read.

This prevents an earlier large GIF's baseline hash reads from passing checks for a later small reference GIF at the same path. Baseline hashing uses a 1 MiB stream buffer, queued trace batches drain without the idle polling delay, and filename correlation indexes unresolved operations by key instead of scanning all pending operations during every rundown callback. The five-second drain deadline and honest incomplete reporting remain in place.

The repository skill at `.agents/skills/analyze-lockscreen-logs/SKILL.md` provides a read-only ZIP/JSON analysis script and maintained investigation guidance. Its regression tests use synthetic data; user exports are not stored in the repository.

## Automated verification

### Interpreting file-activity warnings

Genuine independent-application failures warn only when a completed attempt on an intended target started after its successful verification. The finding includes the operation, raw status, timestamps and process/session identity; it does not claim playback failed. Earlier, crossing or untimed attempts are informational. System I/O has an unknown initiating application. Successful writes alone do not establish changed bytes, and appear with setup activity in the collapsed **Other file activity** group.

The additive schema-2 aggregate fields `FailureTiming` and `ModificationTiming` retain immutable latest-started and latest-completed witnesses plus an untimed count, independently of raw-detail retention. `FastIoFallbacks` separates `STATUS_FLT_DISALLOW_FAST_IO` from genuine failures without counting it as successful I/O. Missing fields remain unknown in older exports. Totals still cover the whole test. Raw legacy records support an all-before classification only when collection is complete, category totals reconcile and timing is valid; retained positive evidence remains useful when collection has gaps.

Hash-change findings compare fresh reads from snapshots begun after verification with the verified applied hash, including the AfterApply snapshot. The expected replacement of baseline contents is excluded. A real temporary mismatch remains visible even if the final file matches again. Missing-file findings require a complete inventory after verification.

### Running the checks

Run from the repository root using .NET 9:

```powershell
dotnet build LockScreenGif/LockscreenGif.csproj --configuration Debug -p:Platform=x64
dotnet run --project Tests/Diagnostics.Tests/Diagnostics.Tests.csproj --configuration Release
dotnet run --project Tests/ApplyPipeline.Tests/ApplyPipeline.Tests.csproj --configuration Release
dotnet run --project Tests/ProcessTracing.Tests/ProcessTracing.Tests.csproj --configuration Release
dotnet run --project Tests/Session.Tests/Session.Tests.csproj --configuration Release
```

The console harnesses exercise production classes against temporary files. Windows session and apply operations in lifecycle tests are test doubles; the apply-file tests explicitly disable elevation. Permission regressions use injected failures, an unelevated echo peer, and mocked native access commands to verify one-helper reuse and the original per-path repair scope. They never apply a real lock screen, lock the desktop, or change cache permissions.

To inspect an existing ZIP with the current C# analyzer without extracting or modifying it, run `dotnet run --project Tests/Diagnostics.Tests/Diagnostics.Tests.csproj -c Release -- --analyze-report "path/to/report.zip"`. The normal regression suite uses synthetic evidence; user reports must not be committed as fixtures.

## Native collector and packaging verification

These opt-in fixture tests request elevation but do not apply a lock screen or alter cache permissions:

```powershell
./Tests/Run-NativeTracing.ps1 -Mode Collector
dotnet publish LockScreenGif/LockscreenGif.csproj -c Release -p:PublishProfile=FolderProfile.pubxml
./Tests/Run-NativeTracing.ps1 -Mode Transport
./scripts/Test-PrivilegedPackaging.ps1
```

Collector mode verifies successful opens, reads, writes, renames and deletes from a known child, process attribution, byte counts, shutdown/progress metadata, rejection of an active collector, and clean restart. Transport mode stages an unelevated test host beside the packaged helper, exercises production peer authentication and one launch, rejects an out-of-scope permission request, captures a fixture read and verifies helper exit. Explicit verification reports are written under the test project's ignored `bin` directory; the app itself never does this.

The helper is built into `Helpers/Privileged` for both ordinary builds and publishing, with TraceEvent restricted to version 3.2.6. The MSI has explicit component/file/feature entries for all 18 runtime files plus the app's contracts assembly. CI checks the dependency version, inventory and executable startup before building the MSI.

Fixture checks do not verify an installed interactive app or a real lock/unlock cycle. Complete the interactive acceptance checks separately.

## Real-machine acceptance checks

These checks require an interactive Windows installation and are not replaced by the isolated tests:

1. Test a reference animation and a reported failing source on affected and working Windows builds. Record the initial lock screen separately from the sign-in background and the display after waking.
2. Run a test with the Windows API step off, then enable it and start another test using the same source. Export both reports. An API-on run can influence later runs; restore the baseline before assessing causality.
3. Verify the automatic five-second countdown, immediate Lock now, and Cancel (including Escape). Cancel must leave monitoring active without reopening the dialog. Failed applies must not show it. Lock manually during the countdown, or navigate away/close the app, and verify it does not lock again. Verify current-session lock/unlock notifications with Win+L and Lock now. Navigate away and back during monitoring; the run must continue. Stop during preparation, applying, waiting, and post-unlock collection.
4. Check existing readable caches, restricted caches, declined elevation, missing destinations, partial replacement, and concurrently locked files. Only demonstrable permission problems should request repair.
5. Change resolution/display configuration and exercise display off/on, sleep/resume, and disconnect/reconnect. Reports must disclose gaps rather than claim continuous coverage.
6. Close the app during collection or applying, then reopen and verify that Diagnostics starts without a previous result. Do not start a second apply while an OS image-setting operation is still in flight.
7. Compare exported relevant operations with Process Monitor during actual lock/unlock cycles, with the Windows API step off and on. Confirm successful reads include completion status/bytes and process identity; do not infer playback from reads. Exercise another ETW tool concurrently, another app instance, helper/parent exit, alternate administrator credentials and restricted/readable caches. Check for no leaked app-owned ETW session or automatic ETL files.
8. Inspect `ProcessTrace.Shutdown` in new exports. Compare native stop return, callbacks finished at each checkpoint, last ETW event timestamp, `StageAtDeadline`, consumer return, and final hash-read times. A normal stop should finish without a deadline marker; a timeout must remain incomplete after cleanup. Native buffer totals must not be interpreted as consumer completion.
9. Verify WinUI light/dark/high-contrast themes, keyboard navigation, focus, screen-reader labels, narrow window widths, and 100–200% scaling. Export through the real file picker and inspect the ZIP for personal information.
