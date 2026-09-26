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

Working reference and selected-GIF cycles demonstrated the noise pattern: DLL host/LogonUI failures and modifications occurred during preparation before successful verification, and raw `0xC01C0004` records represented fallback attempts. Treating every external error or modification as a warning misrepresented those cycles. This does not establish that the same process/status is harmless in every report; use operation, timing, attribution, content evidence, and collector completeness each time.

The diagnostic page intentionally omits playback-observation controls. Ask for the visible result and surface alongside a ZIP when not already supplied. An empty exported observation is not a failed test. Short lock/unlock tests also cannot establish the cause of next-day cache reversion; that needs evidence captured during the later event.

## Coverage counters

- `EventsLost`: ETW collection loss.
- `QueueDropped`: helper transport overflow. Aggregates can still be complete for processed events even when detailed records were dropped.
- `OmittedOperations`: client detail retention limit (10,000 operations / 16 MiB). Do not reconstruct whole-trace totals from the retained subset.
- `OmittedAggregates`: aggregate capacity exceeded; per-file summaries may be absent.
- `UnmatchedOperations`, `UnresolvedProcesses`: relevant operations without complete correlation/identity.
- `UnresolvedPaths`, `UnmatchedCompletions`: can include unrelated system activity whose relevance was never established. They do not by themselves prove missed GIF reads or a playback failure.

Treat actual loss, helper disconnect, early stop, watchdog, and drain timeout as incomplete tracing even when useful positive evidence survives. Trace completion and successful animation are separate outcomes.

## Established failure pattern: large baseline GIF

A report showed ten files each with 2,228 System reads / 145,966,787 bytes. The baseline files were each exactly that size. `ceil(145966787 / 65536) == 2228`: the count represented one sequential pass in 64 KiB chunks, not thousands of complete reloads of the small replacement GIF. The System operations were interleaved with app reads and all ended before applying; timing and bytes strongly associated them with baseline hashing.

The old findings matched only filename and final verification flags, falsely awarding checkmarks to those earlier reads. The actual post-lock evidence was two LogonUI reads totaling the new GIF's 43,495 bytes. The fix uses verification/read timestamps, treats System activity conservatively, and keeps whole-trace totals out of the applied-image success text.

The same run had transport overflow and a drain timeout. Fixed-delay polling of only 128 records every 200 ms limited draining to roughly 640 records/s. Backlogged batches now drain without the idle delay. Baseline reads use a 1 MiB stream buffer to reduce operation volume. Filename correlation uses a pending-key index to avoid scanning all pending operations for every unrelated rundown name. These changes require new real-cycle evidence before claiming that every timeout/overflow is resolved.

## Compare collector workload before declaring a performance fix verified

A subsequent reference-GIF cycle completed without ETW loss, transport loss, omissions, or a drain timeout. It captured the two completed application reads after verification and freshly verified all intended copies after unlocking. That is useful acceptance evidence for that cycle.

However, its baseline copies were already 43,495 bytes, whereas the earlier overflowing run inspected 145,966,787-byte copies. Zero drops with small baseline files does not by itself validate the large-file optimization or establish that code changes caused the improvement. Compare baseline sizes, inventory completeness, source size, duration, and operation volume before attributing performance changes. The helper's `BaselineInventory` exposes known file counts and bytes separately from `SourceBytes`; missing sizes must stay unknown. A later test that begins with a large cached GIF is needed to exercise that workload again.

## Drain timeout without a large workload or recorded drops

The older message `Trace draining exceeded five seconds.` originated in `EtwFileCollector.StopCoreAsync`; the current collector delegates this wait to `TraceWorkerDrain`. After calling `NativeTraceSession.Stop()`, it waits up to five seconds for the entire consumer worker. That task includes `source.Process()`, disposal, and final correlation. The message alone does not identify which stage was delayed, prove a full transport queue, or measure five seconds of active event processing. `DiagnosticProcessTrace` final IPC draining is a separate stage with its own failure message.

A timeout has also occurred with small baseline files and zero recorded ETW/transport loss or retention omissions. Do not reuse the large-baseline diagnosis simply because the warning text matches. Windows documents that a real-time `ProcessTrace` call may take several seconds to return after the session stops; this is a possible contributor, not a report-proven cause. See [ProcessTrace](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-processtrace).

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

## Warnings in successful cycles

Two user-confirmed successful cycles, one using the reference GIF and one a selected GIF, produced access-failure and external-modification warnings despite verified copies, independent post-verification reads, and matching fresh final hashes. These warnings need temporal and attribution context:

- All retained external failure operations in these examples finished before the first destination was verified. Compare `Timestamp` and `CompletedAt` with the Windows API events and each file's `VerifiedAt`; a whole-trace failure total is not evidence that the applied GIF could not be read. Missing-file and privilege failures remain real operation results, but do not establish an unresolved apply or playback failure when later operations succeed.
- `STATUS_FLT_DISALLOW_FAST_IO` (`0xC01C0004`) selects an I/O fallback path, not necessarily a terminal write failure. In these examples, such statuses were followed by successful writes on the same path and process lifetime. Preserve the raw status and look for subsequent outcomes; do not automatically treat fallback as successful either. Microsoft documents that the I/O manager may reissue the operation through the IRP path: [Fast I/O fallback](https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/disallowing-a-fast-i-o-operation-in-a-preoperation-callback-routine), [NTSTATUS values](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/596a1078-e883-4972-9bbc-49e60bebca55).
- Post-verification modifications in these examples were System writes, with byte counts consistent with the selected GIF and matching hashes afterward. Cache writeback is a plausible explanation, not a proven initiator. The same PID-4 attribution caution used for reads applies to writes; do not label them as proof of another application overwriting the GIF.
- Older `DiagnosticTraceFindings` summarized whole-trace failures/modifications, and `TraceHashCorrelation` could describe the expected Baseline-to-AfterApply hash change. The revised findings use operation timing and verified applied hashes: expected application replacement is excluded, and modifications alone remain informational. Aggregate `LastAt` is still the last activity of any kind, not a failure/write timestamp. If bounded timing is absent and retained records cannot reconcile the old totals, preserve uncertainty rather than assigning an entire aggregate to a phase.
- Multiple processes named LogonUI can belong to different Windows sessions. Compare process lifetime and `SessionId` with the app session before connecting an operation to the observed lock cycle; a process name alone is insufficient.

Both cycles also supplied normal shutdown checkpoints: native STOP succeeded in about 120 ms, the consumer returned normally about 160 ms later, and final inspection activity was present. This validates the instrumentation for these cycles, not the cause or resolution of a previous timeout. Their baseline files were small, so they do not exercise the previously problematic large-baseline workload.

For reports from affected machines, obtain the visible symptom and surface in accompanying text if observation controls are absent. Use a selected/reference GIF pair to narrow source-specific versus machine-wide behavior. Both runs with the API enabled cannot isolate the API's effect; an API-on run can change the next run's baseline. Normal monitoring stops shortly after unlock, so a report cannot attribute a next-day reset that falls outside its capture window.

### Real-cycle acceptance of the revised findings

Three subsequent user-confirmed animated cycles exercised the reference and selected GIF with the API off, and the reference with the API on. Every intended copy matched fresh AfterApply and AfterUnlock reads, independent post-verification reads were captured, and collection stopped normally without recorded losses or omissions. Exported findings and replay through the current analyzer both contained zero warnings. This is acceptance evidence for those cycles, with playback established separately by the user's observation.

All aggregates contained the new optional fields, and their failure/modification witnesses, untimed counts, and fallback totals reconciled with retained operations. Check this on new-build acceptance reports; zero warnings alone does not prove that the helper supplied the new evidence. Replay reports with `dotnet run --no-build --project Tests/Diagnostics.Tests/Diagnostics.Tests.csproj -c Release -- --analyze-report "path/to/report.zip"` after building the current test executable.

The API-on cycle retained setup failures, one fallback with `Succeeded=false`, and a later System write while keeping its activity group informational. Sidecar and temporary paths had no applied-file verification boundary: their `Unknown` phase did not mean operation timestamps or collection were missing. The API-off cycles had no external failure/modification/fallback activity and appropriately omitted the empty group. These sequential runs do not establish the API's general necessity or isolate its causal effect; inspect configuration and baseline contents as well as observed output.

## Code and verification map

Paths below are relative to the repository root.

| Concern | Code | Isolated test project |
| --- | --- | --- |
| Commit and verification timestamps | LockScreenGif/Services/Lockscreen/VerifiedCacheWriter.cs | Tests/ApplyPipeline.Tests |
| Read findings and icons | LockScreenGif/Services/Diagnostics/DiagnosticImageReadFinding.cs, DiagnosticTraceFindings.cs; LockScreenGif/ViewModels/DiagnosticFindingViewModel.cs | Tests/Diagnostics.Tests |
| Baseline and final hashes | LockScreenGif/Services/Diagnostics/CacheFileReader.cs, CacheCollector.cs | Tests/Diagnostics.Tests |
| Polling, bounded details and shutdown | LockScreenGif/Services/Diagnostics/DiagnosticProcessTrace.cs, DiagnosticRun.cs | Tests/Session.Tests |
| ETW correlation, aggregates and ownership | LockscreenGif.Privileged.Helper/Tracing/ | Tests/ProcessTracing.Tests |
| Shutdown stages, callback progress and partial drain | LockscreenGif.Privileged.Helper/Tracing/{TraceProgressRecorder,TraceWorkerDrain,EtwFileCollector.Shutdown}.cs; LockScreenGif/Services/Diagnostics/DiagnosticTraceShutdownSummary.cs | Tests/ProcessTracing.Tests/ShutdownProgressTests.cs, Tests/Diagnostics.Tests/ShutdownReportTests.cs |
| Report schema and redaction | LockscreenGif.Privileged.Contracts/TraceEvidence.cs; LockScreenGif/Services/Diagnostics/DiagnosticReportWriter.cs, DiagnosticRedactor.cs | Tests/Diagnostics.Tests |

Run an isolated project with `dotnet run --project Tests/<project>/<project>.csproj -c Release`. Build the WinUI app with `dotnet build LockScreenGif/LockscreenGif.csproj -c Debug -p:Platform=x64`. The opt-in native harness and real-machine acceptance procedure are in `docs/diagnostics.md`; do not silently trigger elevation/locking when asked only to analyze a report.

Authoritative semantics: [FileIo_ReadWrite](https://learn.microsoft.com/en-us/windows/win32/etw/fileio-readwrite), [FileIo_OpEnd](https://learn.microsoft.com/en-us/windows/win32/etw/fileio-opend), [file caching](https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching), [ProcessTrace shutdown](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-processtrace).
