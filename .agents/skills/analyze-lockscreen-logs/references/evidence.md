# Diagnostic evidence reference

## Schema and interpretation

Exports currently use schema 2, with additive fields. Check field presence instead of assuming every schema-2 report contains newer timing fields.

- `Gif`: selected source size/hash, structure, loop/timing inspection. It does not prove decoding.
- `ApplyResult.Files`: intended path, copy/verification status, observed SHA-256, error, and (newer reports) `VerifiedAt`. This is recorded after readback of the committed destination succeeds.
- `Snapshots`: named capture boundaries and per-file length, hash, stability, hash provenance, and `HashReadAt`. A snapshot timestamp is capture start; individual files can finish later. A reused hash does not establish fresh post-unlock contents.
- `Events`: application phase transitions and observed WTS lock/unlock notifications. A requested lock is not an observed lock.
- `ProcessTrace.Operations`: bounded detailed operations with start/completion times, identity, requested/completed bytes, NTSTATUS, and app attribution. These are file I/O operations, not animation frames or necessarily physical disk reads.
- `ProcessTrace.Files`: per-path/process-lifetime aggregates spanning the entire collection. `Reads` and `ReadBytes` include baseline and old contents. `FirstAt`/`LastAt` include non-read operations and cannot prove a late read. Newer `LastReadStartedAt`/`LastReadCompletedAt` describe the latest-started successful, positive-byte read and survive raw-record omissions.

A read qualifies for the applied-image finding only when it starts after that file's successful `VerifiedAt` and has a valid completion. A read begun on the previous file and completed after replacement does not qualify. Missing timestamps are unknown, not zero. The script can use an explicitly labeled legacy verification event or conservative AfterApply boundary for investigation; production findings do not guess these timestamps.

Exclude the app, helper, and their tagged children from independent access evidence. PID 4 (System) may service the app's inspection I/O; it does not establish an independent application reader. Keep that activity in exported evidence without claiming that it loaded the lock-screen image. Do not identify the renderer from a process name or cache access alone.

An attempted read, zero-byte read, pending status, or missing completion is not a successful data read. Read EOF (`0xC0000011`) with no bytes is normal stream termination; preserve the raw status without treating it as an image failure. Do not generalize this exception to writes or genuine access-denied failures.

`STATUS_FLT_DISALLOW_FAST_IO` (`0xC01C0004`) asks Windows to use the ordinary IRP path instead of fast I/O. It is neither a genuine access failure nor a successful data read. Preserve the raw operation; a separate successful read is still required. Microsoft documents that an equivalent IRP request may follow, not that every fallback necessarily succeeds: [Fast I/O fallback semantics](https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/disallowing-a-fast-i-o-operation-in-a-preoperation-callback-routine), [NTSTATUS value](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/596a1078-e883-4972-9bbc-49e60bebca55).

## Activity phases and diagnostic noise

New `TraceAggregate` objects have nullable `FastIoFallbacks`, `FailureTiming`, and `ModificationTiming`. An initialized fallback counter (including zero) marks the new semantics: `Failures` excludes fallback attempts. Null or absent `FastIoFallbacks` means the old failure counter may include them. Do not subtract only retained fallback records from a potentially truncated whole-test total and present the result as an exact genuine-failure count.

Each timing summary keeps two bounded witnesses, `LatestStarted` and `LatestCompleted`, with `StartedAt`, `CompletedAt`, `Operation`, and `Status`, plus `UntimedCount`. They need not describe the same operation: a slower earlier start may complete after a later start. These summaries are collected before detail retention and survive dropped/omitted raw records. `FirstAt`/`LastAt` cover unrelated categories too and cannot replace these witnesses.

The helper's `ActivityByFileAndProcess` uses four phases relative only to that intended path's successful `ApplyResult.Files[].VerifiedAt`. Its legacy read-boundary guesses are never used for activity warnings:

- `AfterVerification`: a genuine category operation has valid start/completion times and starts at or after verification. This positive observation survives collection gaps.
- `OverlapsVerification`: a known operation starts before verification and completes at or after it, with no qualifying later-started witness. It cannot be assigned wholly to the applied GIF.
- `BeforeVerification`: all observed operations in the category are timed and the latest completion precedes verification. New bounded summaries support this classification even if raw details are missing; wording must remain about observed activity and the separate collection warning remains.
- `Unknown`: verification, timing, or trustworthy completeness for this classification is unavailable. Missing, reversed, or incomplete timestamps and pending operations cannot become positive evidence.

For older reports, retained operations are matched by case-insensitive path, PID, and process lifetime. They can establish positive after/overlap observations even with gaps. All-before reconstruction additionally requires `State=Completed`, no collection-gap counters, exact reconciliation against the aggregate category count, and valid timing for every reconciled operation. Old failure counts reconcile genuine failures plus Fast I/O fallbacks, while only genuine failures supply failure witnesses. Unknown paths/unmatched system completions alone are unscoped uncertainty, not collection loss. Missing new fields remain unknown.

Only a genuine failure after verification on an intended path from an independently identified application (`!IsApp`, attribution resolved, PID greater than 4) is an image-access warning. App/helper operations, preparation-time errors, System activity, uncertain attribution/timing, and missing reads remain informational. All modification activity is informational: even a post-verification write is not proof that the selected bytes changed. Actual stable, fresh post-verification hash mismatches remain separate warnings, along with apply errors and genuine coverage problems.

Whole-test counters are labeled as such. A latest witness proves an event in a phase, not how many events occurred there. The analyzer keeps read and shutdown analysis separate and does not reconstruct totals from retained operations.

Sidecar and temporary paths may have no applied-file verification boundary. Their `Unknown` phase does not by itself mean operation timestamps or collection are missing. Routine activity belongs in the informational **Other file activity** group; omit the group when it has no entries.

The diagnostic page intentionally omits playback-observation controls. Ask for the visible result and surface alongside a ZIP when not already supplied. An empty exported observation is not a failed test. Short lock/unlock tests also cannot establish the cause of next-day cache reversion; that needs evidence captured during the later event.

## Regular apply verification

`LockscreenVerificationService` reuses the diagnostic trace collector for the regular apply flow, without reapplying or running full cache inventories. Monitoring begins only after **Lock now** is selected, before requesting the lock. The result requires a successfully verified target and a completed positive-byte read started after that target's `VerifiedAt`, attributed to `LogonUI.exe` in the app's Windows session. Other readers can qualify for the diagnostic page's broader external-read finding but cannot qualify for the regular flow's LogonUI confirmation. Retained operations can establish a read even when their aggregate was omitted.

Process names have two native sources: the startup `Process.GetProcesses()` snapshot uses `ProcessName` (for example, `LogonUI`), while ETW process events supply an image filename (for example, `LogonUI.exe`). Match those exact names case-insensitively while still requiring resolved process/session attribution. Requiring the extension alone can miss an already-running LogonUI process.

**Later** completes the apply without collecting evidence. The normal check stops and drains after the observed unlock; if its five-minute capture limit expires while locked, it releases the collector and retains the session listener until unlock or app closure. No captured read remains inconclusive, including when Windows reused an image or tracing was unavailable. A positive read survives collection gaps. The short result describes file access, while the subsequent Windows notification confirms only that the GIF was applied. This check has no fresh after-unlock hash inventory and is not a diagnostic export.

## Coverage counters

- `EventsLost`: ETW collection loss.
- `QueueDropped`: helper transport overflow. Aggregates can still be complete for processed events even when detailed records were dropped.
- `OmittedOperations`: client detail retention limit (10,000 operations / 16 MiB). Do not reconstruct whole-trace totals from the retained subset.
- `OmittedAggregates`: aggregate capacity exceeded; per-file summaries may be absent.
- `UnmatchedOperations`, `UnresolvedProcesses`: relevant operations without complete correlation/identity.
- `UnresolvedPaths`, `UnmatchedCompletions`: can include unrelated system activity whose relevance was never established. They do not by themselves prove missed GIF reads or a playback failure.

Treat actual loss, helper disconnect, early stop, watchdog, and drain timeout as incomplete tracing even when useful positive evidence survives. Trace completion and successful animation are separate outcomes.

## Baseline reads and collector workload

Compare whole-trace read bytes with each baseline file's length. Many chunked reads can represent one sequential inspection pass, rather than repeated loads of the applied GIF. For fixed-size chunks, `ceil(file length / chunk size)` estimates the operation count for a pass. Use operation timing and app attribution to distinguish inspection from independent access after verification.

Baseline hashing uses a 1 MiB stream buffer. Backlogged trace batches drain without an idle polling delay, and filename correlation indexes pending operations by key. When assessing collector performance, compare baseline sizes, inventory completeness, source size, duration, and operation volume. A small selected GIF can replace a much larger cached file; zero drops with a small baseline do not validate a large-baseline workload. The helper's `BaselineInventory` exposes known file counts and bytes separately from `SourceBytes`; missing sizes remain unknown.

## Drain timeout without a large workload or recorded drops

The older message `Trace draining exceeded five seconds.` originated in `EtwFileCollector.StopCoreAsync`; the current collector delegates this wait to `TraceWorkerDrain`. After calling `NativeTraceSession.Stop()`, it waits up to five seconds for the entire consumer worker. That task includes `source.Process()`, disposal, and final correlation. The message alone does not identify which stage was delayed, prove a full transport queue, or measure five seconds of active event processing. `DiagnosticProcessTrace` final IPC draining is a separate stage with its own failure message.

A timeout does not establish a large workload or transport overflow. Inspect the workload and shutdown measurements even when loss counters are zero. Windows documents that a real-time `ProcessTrace` call may take several seconds to return after the session stops; this is a possible contributor, not a report-proven cause. See [ProcessTrace](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-processtrace).

Check the latest `Operations.Timestamp` and `CompletedAt`, plus aggregate `LastAt`, against final snapshot `HashReadAt` for fresh hashes. If even the app's final inspection reads are absent from an otherwise untruncated trace, there is evidence that the report lacks activity near shutdown. A quiet interval by itself is not proof of a stall; omissions or unresolved correlation can also remove those records. `ProcessTrace.EndedAt` is assigned during worker cleanup and must not be treated as the last consumed event timestamp or a guarantee of full coverage. Zero loss counters do not establish successful draining.

At the deadline the collector calls `StopProcessing`, so keep tracing incomplete while preserving earlier positive read/copy evidence. Do not claim the timeout is harmless, that animation failed, or that a longer timeout fixes the underlying issue. Newer reports describe the same timeout as file activity monitoring not finishing shutdown within five seconds; older reports retain the original message.

### Shutdown measurements in newer schema-2 reports

`ProcessTrace.Shutdown` is optional in older exports. The analysis script's `TraceShutdown` section exposes its presence, lifecycle timestamps, callback progress, and the final fresh-hash boundary. Missing measurements remain null rather than fabricated zeroes.

- `StopRequestedAt`, `NativeStopStartedAt`, `NativeStopReturnedAt`, `DrainWaitStartedAt`, `DrainDeadlineExceededAt`, `ConsumerReturnedAt`, and cleanup times separate the stop call, consumer wait, and disposal/correlation. Native-stop and deadline elapsed milliseconds use a monotonic clock.
- `NativeStopStatus` is the actual Windows result when a call ran; 0 is success and 4201 means the session was already absent. `NativeStopAttempted=false` means cleanup was already done locally and no native call ran; a missing status is not error code zero.
- `NativeStopBuffers` holds native session buffer/loss statistics returned by STOP. They are not consumer buffer counts, a queue depth, or proof that callbacks processed every buffer.
- `Current`, `AtStopRequest`, `AtNativeStopReturn`, and `AtDrainDeadline` are fixed-size progress snapshots. They count all event callbacks, including unrelated events, without retaining payloads. `LatestEventTimestamp` is the newest ETW event time; callback start/finish times are wall-clock delivery/processing times. Do not treat total callbacks as image reads.
- An increasing finished-callback count between native stop and deadline proves consumer progress in that interval. No increase alone does not distinguish idle native waiting, a blocked callback, or cleanup. `StageAtDeadline` and `CallbackInProgress` narrow that question; they do not provide a thread stack or identify the precise blocking operation.
- `ConsumerCompletedNormally=false` records `Process()` returning after a stop-processing request. Null plus `ConsumerFailureType` records an exception. `ForcedStopGraceExceededAt` means even the subsequent two-second wait did not finish; the helper allows retrieval of partial evidence instead of discarding these measurements behind a generic stop error.

All checkpoint objects are snapshots. The eventual `WorkerStage=Finished` must not overwrite the recorded stage/progress at the deadline, and completed cleanup must not turn an incomplete trace into a completed one.

## Hash comparison and process identity

Compare stable, fresh reads from snapshots begun after verification against the verified applied hash, including AfterApply. Exclude the expected Baseline-to-applied-GIF transition. A temporary mismatch remains relevant even when a later snapshot matches again. Missing-file conclusions require a complete inventory after verification.

A System write with matching hashes afterward does not identify the initiating application or prove an overwrite. Treat cache writeback as a possible explanation only. Multiple processes with the same name can belong to different Windows sessions; compare process lifetime and `SessionId` with the app session before connecting activity to a lock cycle.

## Report comparison and validation

Obtain the visible symptom and surface in accompanying text. Use a selected/reference GIF pair to investigate source-specific versus machine-wide behavior. Compare API-on and API-off settings with controlled baseline contents: an API-on run can change the next run's baseline. Normal monitoring stops shortly after unlock, so a report cannot attribute a later reset outside its capture window.

For new-build validation, check that aggregates contain the expected optional fields. When raw coverage is complete, reconcile failure/modification witnesses, untimed counts, and fallback totals with retained operations. Zero warnings alone does not prove the helper supplied the timing evidence. Compare exported findings with replay through the current analyzer using `dotnet run --project Tests/Diagnostics.Tests/Diagnostics.Tests.csproj -c Release -- --analyze-report "path/to/report.zip"`. Confirm playback separately through visual observation.

## Save/export errors after bytes were written

A `COMException` with HRESULT `0x80070490` at `CachedFileManager.CompleteUpdatesAsync` establishes that the provider-update completion call failed; it does not prove the preceding copy failed or the GIF encoder produced invalid output. Compare the save stack with the actual export bytes and dimensions before attributing it to generation, preview playback, or lock-screen application.

Check `StorageFile.Provider.Id` before assuming a provider app owns the destination. `CompleteUpdatesAsync` can return element-not-found for plain local files in an unpackaged desktop caller even when bytes are written into the existing file; replacement is not required to trigger the error. The HRESULT alone does not establish that `CopyAndReplaceAsync` invalidated the destination identity. A nonempty path or `IsAvailable=true` also does not distinguish a plain local file from cached cloud storage.

`PickedFileWriter` skips deferred provider updates only for exact, case-insensitive `computer` and `local` provider IDs. All other IDs, including absent/unknown metadata, retain the defer/complete protocol and genuine status/COM failures. It writes/truncates the existing destination stream and flushes/closes it, preserving the picker item's metadata. `IsEqual` protects same-file saves, including provider items without filesystem paths. Bytes existing locally alone do not prove cloud-provider synchronization succeeded. If both writing and update cleanup fail, the original write error remains primary and the cleanup error is logged separately.

The synthetic `Tests/PickedFileWriter.Tests` harness covers those relationships without native APIs or user files. For native validation, use synthetic files for both a new export and replacement of longer contents, compare every byte, and inspect actual provider metadata. Local storage tests do not prove picker UI or cloud-provider behavior; validate available provider destinations separately. API contracts: [StorageProvider.Id](https://learn.microsoft.com/en-us/uwp/api/windows.storage.storageprovider.id), [CompleteUpdatesAsync](https://learn.microsoft.com/en-us/uwp/api/windows.storage.cachedfilemanager.completeupdatesasync), [IsEqual](https://learn.microsoft.com/en-us/uwp/api/windows.storage.storagefile.isequal).

## Generated GIF resolution

Compare the saved GIF's logical-screen width/height with the exported PNG frame dimensions and the selected output resolution. Correct FFmpeg frame dimensions do not prove the encoder kept them: Gifski's unset width/height defaults can automatically downscale frames in `dimensions_for_image`. `GifSkiService` supplies both dimensions from the first exported PNG's IHDR to preserve the actual frame size. See `Tests/VideoEditing.Tests/ResolutionTests.cs` and the [Gifski encoder source](https://raw.githubusercontent.com/ImageOptim/gifski/main/src/lib.rs). A preview's display size or a source-resolution label does not establish saved-file dimensions.

## Code and verification map

Paths below are relative to the repository root.

| Concern | Code | Isolated test project |
| --- | --- | --- |
| Save/export bytes and provider completion | LockScreenGif/Services/PickedFileWriter.cs | Tests/PickedFileWriter.Tests |
| Commit and verification timestamps | LockScreenGif/Services/Lockscreen/VerifiedCacheWriter.cs | Tests/ApplyPipeline.Tests |
| Read findings and icons | LockScreenGif/Services/Diagnostics/DiagnosticImageReadFinding.cs, DiagnosticTraceFindings.cs; LockScreenGif/ViewModels/DiagnosticFindingViewModel.cs | Tests/Diagnostics.Tests |
| Baseline and final hashes | LockScreenGif/Services/Diagnostics/CacheFileReader.cs, CacheCollector.cs | Tests/Diagnostics.Tests |
| Polling, bounded details and shutdown | LockScreenGif/Services/Diagnostics/DiagnosticProcessTrace.cs, DiagnosticRun.cs | Tests/Session.Tests |
| ETW correlation, aggregates and ownership | LockscreenGif.Privileged.Helper/Tracing/ | Tests/ProcessTracing.Tests |
| Shutdown stages, callback progress and partial drain | LockscreenGif.Privileged.Helper/Tracing/{TraceProgressRecorder,TraceWorkerDrain,EtwFileCollector.Shutdown}.cs; LockScreenGif/Services/Diagnostics/DiagnosticTraceShutdownSummary.cs | Tests/ProcessTracing.Tests/ShutdownProgressTests.cs, Tests/Diagnostics.Tests/ShutdownReportTests.cs |
| Report schema and redaction | LockscreenGif.Privileged.Contracts/TraceEvidence.cs; LockScreenGif/Services/Diagnostics/DiagnosticReportWriter.cs, DiagnosticRedactor.cs | Tests/Diagnostics.Tests |

Run an isolated project with `dotnet run --project Tests/<project>/<project>.csproj -c Release`. Build the WinUI app with `dotnet build LockScreenGif/LockscreenGif.csproj -c Debug -p:Platform=x64`. The opt-in native harness and real-machine acceptance procedure are in `docs/diagnostics.md`; do not silently trigger elevation/locking when asked only to analyze a report.

Authoritative semantics: [FileIo_ReadWrite](https://learn.microsoft.com/en-us/windows/win32/etw/fileio-readwrite), [FileIo_OpEnd](https://learn.microsoft.com/en-us/windows/win32/etw/fileio-opend), [file caching](https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching), [ProcessTrace shutdown](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-processtrace).
