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

## Apply analytics before destination registration

For `lockscreen_apply_completed`, first check `workflow`: diagnostic attempts are not the normal apply-success denominator. Trace the implementation at the event's `app_version` commit when possible; current analytics can expose errors that older builds did not retain.

`target_count` is `ApplyResult.Files.Count`, not a direct cache inventory. The pipeline adds those entries only after destination discovery returns successfully. A failed event with all four file counts zero means no destinations were registered and no cache copies were attempted; it does not prove the cache is physically empty. Discovery can throw after finding some folders, and source hashing can fail before discovery on an API-off attempt. `failed_count` counts unsuccessful file results, so zero does not contradict an overall failed operation. Source size/dimensions show that the header was read, not that hashing, full decoding, or playback succeeded.

`api_requested=false` means the optional image-setting step was disabled. Its corresponding `api_completed=false` is not an API error. `api_completed=true` is set after the awaited Windows image-setting call succeeds; an otherwise failed result with no registered destinations then points to the subsequent cache-settling/discovery path. The API may have changed the image even though the cache replacement failed. Neither state establishes animation.

Use elapsed time only after checking that build's implementation. With the current `CacheLayout.WaitForSettlingAsync` loop, an ordinary stable result needs four 500 ms delays (the first sample establishes the baseline). An API-completed, non-cancelled failed apply shorter than two seconds therefore indicates the settling loop exited through its exception path before discovery also failed. That narrows the stage but does not distinguish access denied, a missing directory, path validation, or another inventory error. An accessible empty cache would still undergo the normal settling samples.

Inspect `CachePermissions.ValidatePath` when access is denied: it reads attributes for the cache path and every ancestor to reject reparse points. Older clients repeated the same failing check before reaching the permission helper. The current repair path defers only denied attribute reads to the helper while still rejecting paths outside the cache and visible links. `ProtectedCacheMetadata` inspects denied paths using a temporary backup-enabled impersonation token and a metadata handle opened with `OPEN_REPARSE_POINT`; it does not enable a process-wide privilege or follow the final link. The helper grants the initiating SID non-inherited ReadAttributes (with the ACL API's synchronization bit) on the fixed user parent, preserving other ACL entries and inheritance. It uses the ACL API, with nonrecursive takeown only when needed, because icacls can still fail after ownership changes when attributes remain unreadable. Requested cache targets retain Read/Execute or Modify grants and nonrecursive ownership fallback. No SystemData or Everyone grants are added. Strict client validation runs again after repair and before discovery/writes; a helper success cannot authorize skipping unresolved attributes or links. See `Tests/ApplyPipeline.Tests/ProtectedMetadataTests.cs`, `Tests/ProcessTracing.Tests/ProtectedMetadataTests.cs`, and the opt-in `Tests/Run-NativePermissions.ps1` fixture. Native fixture success is not proof of a real lock/unlock cycle.

New `lockscreen_apply_completed` failures and partial results carry `apply_failure_reason`: `cache_inaccessible`, `cache_missing`, and `no_destinations` distinguish denied access, an absent required cache directory, and a completed discovery with no destinations. Source, Windows API, discovery, copy, and verification failures have separate bounded categories; uncategorized failures use `unknown`. Success and cancellation omit the property. Classification follows the active operation stage and exception type, never exception text or paths. The first unsuccessful destination supplies a partial result's reason; a later cancellation clears it. See `docs/analytics.md` for the complete contract.

Use `ApplyResult.FailureReason` and the actionable `ApplyResult.Error` for the recovery action. In current builds, detailed exception evidence remains in `Apply: Failed`, `Apply: FileFailed`, and application logs; older `ApplyResult.Error` values can contain the raw exception description. Compare `Apply: WindowsApi`, `Apply: Discovery`, and `Apply: Permissions` events to locate the failure. For companion `$exception` events, correlate by `operation_id` and `error_context=lockscreen_apply`. Older result-returning code kept exceptions locally without sending a cause or companion event; historical counters cannot recover that missing reason. Absent delivery remains inconclusive. Regular apply UI retains the actionable message only for the matching failed/partial attempt and clears it on retry or a changed source.

Event-list exports can omit the properties needed for interpretation. A `*_completed` event name alone does not establish success or failure, workflow, or a saved diagnostic ZIP. Inspect the full `outcome` and operation properties before counting failures or assuming a report was exported. Count distinct installations separately from attempts so one installation's retries do not become multiple affected users. Analytics opt-out makes subsequent silence uninformative about app usage; missing starts or completions in a time-bounded list also cannot establish crashes. Prefer an existing successfully exported report, shared voluntarily, over asking for another diagnostic run solely because its export event is visible.

## Fresh-profile cache discovery

When a user has never selected a lock-screen image, a `DirectoryNotFoundException` from `CacheLayout.ReadFolders` / `Directory.GetDirectories` means the required `SystemData/<user SID>/ReadOnly` path could not be enumerated because a component was missing. This is consistent with an uninitialized per-user cache, but the logs alone do not establish why it was missing. Distinguish this from a completed discovery with zero destinations. Neither case reaches the copy loop; source verification alone is not destination verification.

Compare consecutive API-off and API-on attempts separately. An accepted Windows image-setting call followed by denied attributes on the SID parent establishes an API success and a subsequent discovery/access failure. It does not prove Windows created a complete cache, that its contents are usable, or that animation played. The API may already have changed the image despite the overall apply failure. The cache-settling warning can be followed by a separate fatal discovery error; do not call it a settling timeout when the loop exited on an exception.

For older clients, a stack through `ValidatePath` -> `GrantAsync` -> `FindFoldersAsync` together with the pre-helper validation implementation identifies a blockage before the helper request. Compare wording and the exact build where available; do not assume current source matches a CI build. Deferring validation alone was insufficient: the earlier helper also used ordinary denied ancestor reads, and a child-only grant left client revalidation blocked. The repaired helper handles protected metadata and the fixed SID parent's metadata access explicitly. `PermissionRecoveryTests.StillDeniedAncestorFailsClosed` still ensures genuinely unresolved attributes stop writes; native protected-parent fixtures cover recovery without directory-listing or data rights on the parent.

Preserve a VM snapshot before using Windows Settings > Personalization > Lock screen > Picture and selecting an image as a controlled recovery test. An API-on attempt can already change the baseline even when apply fails, so reproducing the original untouched profile requires a snapshot from before that attempt. Keep manual initialization and a newer-build retest as separate experiments, and retain the visible result and any permission-stage logs. Do not treat the workaround as evidence that first-run handling is fixed. If manual picture selection is followed by the same denied SID-parent attribute check, manual initialization was insufficient to recover that run; focus on the persistent permission-validation failure rather than repeating the setup advice. The earlier API-on attempt already encountered that denial, so do not attribute its onset to the later manual selection. A failure at parent validation still prevents inventory and does not establish whether the child cache is now complete.

For the remaining permission question, collect paired normal/admin reports with `scripts/Get-LockscreenAccessReport.ps1` using the same explicit target SID. Compare `Cache.RootValidation`, each ancestor's `Attributes`/`Acl`, and independent `Cache.FolderEnumeration`; a denied ancestor does not imply the known child cannot be enumerated. Read numeric ACL rights, inheritance and token group/privilege details together rather than inferring effective access from one allow entry. Elevation under another account is identified separately from the target SID. A successful admin probe does not prove the normal process can pass validation after a child-only repair. Missing, access denied, unexpected errors, skipped links and truncated inventories remain distinct. See [the collection guide](../../../docs/cache-access-report.md) and `Tests/LockscreenAccessReport.Tests.ps1`. Keep these private local reports out of commits and analytics.

If both normal and elevated reports enumerate readable cache folders but fail attributes on the SID parent, the first-run missing-cache explanation is insufficient. Confirm the same target SID and a high-integrity administrator token in the second report. SYSTEM ownership and a per-user Read grant explain the need for write recovery, but unchanged snapshots do not establish when ownership changed or that every apply requires elevation. Ordinary elevation does not substitute for protected metadata inspection. Avoid advising whole-app elevation as a proven solution, bypassing link checks, or recursive SystemData grants. Current repair combines scoped parent metadata access and cache write recovery. When interpreting local Permissions events and existing `apply_failure_reason` analytics, compare app versions and workflow; older failure totals cannot establish this cause for every user.

For access-report ACL probes, `Win32Exception` can have generic HRESULT `0x80004005` with native error 5. The collector now exports nullable `Error.NativeErrorCode` and uses native 5/2/3 to distinguish access denied from missing file/path; it preserves the original HRESULT and does not parse localized text. Older reports without that code retain uncertainty at the individual ACL probe, even when an independent attribute failure confirms denied access on the same parent. Synthetic cases in `Tests/LockscreenAccessReport.Tests.ps1` cover generic HRESULTs and misleading messages.

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

### Selected image is visible but frozen

Separate historical application-log failures from the diagnostic session under investigation. An export can include earlier versions that failed discovery or permissions, followed by a repaired version whose copies and fresh final hashes all succeed. Those earlier failures do not explain frozen playback in the later successful apply. Check `UseReference` as well as `Gif`: a reference run tests the bundled animation, not the user's original media. Its metadata is not a pixel-decoding test.

When the user sees the intended image but it stays still, verified copies, unchanged fresh final hashes, and successful independent reads narrow the remaining investigation toward decoding, animation scheduling, and presentation. They cannot identify which of those stages failed. Keep the initial clock/date screen and the PIN/password background separate. A short burst of file reads, or a byte count exceeding the GIF size, does not count rendered frames: Windows may buffer or reread image data. The diagnostic export contains no frame-presentation evidence.

Compare exact OS build and edition on working and failing machines. Matching builds argue against an unconditional build-wide failure; they do not exclude different feature rollouts, policies, or graphics behavior. A linked issue with the same symptom and successful copy logs is corroboration of the symptom, not proof of a common cause. Current-user `UISettings.AnimationsEnabled`, power state, and `SM_REMOTESESSION` are useful environment observations, not a complete description of the secure desktop's renderer.

For a VirtualBox guest, inspect the existing VM's graphics controller, Guest Additions version, acceleration setting, and display-driver startup log read-only. `accelerate3d=off` together with a driver fallback to display-only mode is concrete graphics configuration evidence, not proof that GIF playback requires acceleration. A controlled comparison can change only 3D acceleration after a normal guest shutdown, then compare the same GIF on both lock-screen surfaces; retain a reboot-only control if attributing a recovery to acceleration. Do not power down a running VM or alter its configuration merely to analyze an export. Oracle documents the scope of [Guest Additions hardware-accelerated graphics](https://docs.oracle.com/en/virtualization/virtualbox/7.2/user/guestadditions.html); it does not guarantee lock-screen GIF playback.

### ETW and wall-clock consistency

Before ordering ETW reads against `VerifiedAt`, lock/unlock notifications, or `HashReadAt`, compare `Shutdown` checkpoints' `LatestEventTimestamp` with their callback start/finish wall-clock times. A latest event substantially later than its completed delivery, especially after consumer cleanup, exposes inconsistent clock domains. Zero loss counters and `State=Completed` do not validate that relationship. The analysis helper currently prints these timestamps but does not automatically reject cross-clock phase findings; review them before accepting its after-verification booleans.

Preserve the successful I/O, byte counts, identities, and independently verified hashes, but qualify wall-clock ordering and durations. When possible, compare a reader with the app's destination replacement and readback in the same ETW timeline to establish relative order. Do not subtract a single final offset from the whole run: the offset can change during collection. Clock conversion, guest time synchronization, and virtualized timer behavior are hypotheses until measured; a timestamp anomaly does not by itself explain frozen animation.

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
