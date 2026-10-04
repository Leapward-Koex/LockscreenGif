# Diagnostic evidence reference

## Schema and interpretation

Exports currently use schema 2, with additive fields. Check field presence instead of assuming every schema-2 report contains newer timing fields.

- `Gif`: selected source size/hash, structure, loop/timing inspection. It does not prove decoding.
- `ApplyResult.Files`: intended path, copy/verification status, observed SHA-256, error, and (newer reports) `VerifiedAt`. This is recorded after readback of the committed destination succeeds.
- `Snapshots`: named capture boundaries and per-file length, hash, stability, hash provenance, and `HashReadAt`. A snapshot timestamp is capture start; individual files can finish later. A reused hash does not establish fresh post-unlock contents.
- `Events`: application phase transitions and observed WTS lock/unlock notifications. A requested lock is not an observed lock.
- `ProcessTrace.Operations`: bounded detailed operations with start/completion times, identity, requested/completed bytes, NTSTATUS, and app attribution. These are file I/O operations, not animation frames or necessarily physical disk reads.
- `ProcessTrace.Files`: per-path/process-lifetime aggregates spanning the entire collection. `Reads` and `ReadBytes` include baseline and old contents. `FirstAt`/`LastAt` include non-read operations and cannot prove a late read. Newer `LastReadStartedAt`/`LastReadCompletedAt` describe the latest-started successful, positive-byte read and survive raw-record omissions.

A read qualifies for the applied-image finding only when it starts at or after that file's successful `VerifiedAt` and has a valid completion. A read begun on the previous file and completed after replacement does not qualify. Missing timestamps are unknown, not zero. The script can use an explicitly labeled legacy verification event or conservative AfterApply boundary for investigation; production findings do not guess these timestamps.

Exclude the app, helper, and their tagged children from independent access evidence. PID 4 (System) may service the app's inspection I/O; it does not establish an independent application reader. Keep that activity in exported evidence without claiming that it loaded the lock-screen image. Do not identify the renderer from a process name or cache access alone.

An attempted read, zero-byte read, pending status, or missing completion is not a successful data read. Read EOF (`0xC0000011`) with no bytes is normal stream termination; preserve the raw status without treating it as an image failure. Do not generalize this exception to writes or genuine access-denied failures.

`STATUS_FLT_DISALLOW_FAST_IO` (`0xC01C0004`) asks Windows to use the ordinary IRP path instead of fast I/O. It is neither a genuine access failure nor a successful data read. Preserve the raw operation; a separate successful read is still required. Microsoft documents that an equivalent IRP request may follow, not that every fallback necessarily succeeds: [Fast I/O fallback semantics](https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/disallowing-a-fast-i-o-operation-in-a-preoperation-callback-routine), [NTSTATUS value](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/596a1078-e883-4972-9bbc-49e60bebca55).

## Apply analytics before destination registration

For `lockscreen_apply_completed`, inspect `workflow`, `outcome`, full operation properties and the implementation at `app_version`. Diagnostic attempts are not the ordinary apply-success denominator. A completion event name alone does not establish success or a saved diagnostic ZIP. Count installations separately from retries; analytics opt-out or missing delivery makes subsequent silence inconclusive.

`target_count` is `ApplyResult.Files.Count`. Entries are registered only after destination discovery returns successfully. A failed result with all file counts zero means no destinations were registered or copied, not that the physical cache was empty: source hashing or discovery can fail first. Zero `failed_count` therefore does not contradict an overall failure. Source dimensions show a header read, not full decoding or playback.

`api_requested=false` with `api_completed=false` is a disabled option, not an API error. `api_completed=true` follows the awaited image-setting call and can coexist with later settling/discovery failure; Windows may already have changed the image. Distinguish the settling warning from a subsequent fatal discovery exception. Neither proves animation.

Use `ApplyResult.FailureReason`, actionable `Error`, local `Apply: WindowsApi/Discovery/Permissions/FileFailed/Failed` events and logs to locate the stage. Failed/partial results include bounded `apply_failure_reason`; success/cancellation omit it. Detailed technical evidence remains local. Correlate companion `$exception` by `operation_id` and `error_context=lockscreen_apply`. Older clients may retain only raw local exceptions and counters; missing reasons cannot be reconstructed. See [the analytics contract](../../../../docs/analytics.md).

For denied ancestor checks, inspect `CachePermissions.ValidatePath` and the logged build. Older clients could fail before reaching the helper. Current apply repair defers only denied attributes while preserving scope/link checks. The helper uses a temporary backup-enabled token and a metadata-only `OPEN_REPARSE_POINT` handle, grants non-inherited ReadAttributes on the fixed SID parent, and repairs required cache targets. Strict client validation runs again. See [permission recovery](../../../../docs/diagnostics.md#what-the-evidence-means), `Tests/ApplyPipeline.Tests/ProtectedMetadataTests.cs`, `Tests/ProcessTracing.Tests/ProtectedMetadataTests.cs`, and opt-in `Tests/Run-NativePermissions.ps1`. Fixture success does not prove a real lock/unlock cycle.

## Picture prerequisite and ordinary log exports

Inspect ZIP entries first. Settings log exports can contain only application text logs, without session evidence or prerequisite readback. Use the startup version/commit to inspect that build's checks. `OpenLockscreenSettings` establishes a request to open Settings, not that Picture was selected or detected.

The Picture row combines mode detection with an accessible immediate `LockScreen*` cache folder. Current detection accepts either two explicit DWORD-zero switches or a readable existing Lock Screen key with confirmed absence of `SlideshowEnabled` and explicit DWORD-zero `RotatingLockScreenEnabled`. Older strict builds required both values. Missing keys/Spotlight values, malformed data, read failures and conflicting enabled switches remain unknown or another mode. Cache existence does not infer Picture and the detector does not write registry values.

"Picture mode could not be confirmed" identifies an unsatisfied mode check, not a specific registry value or wrong user selection. Preserve presence, registry kind and bounded raw values from the affected user's session; confirm the visible selection separately. A null `GetValue` is not enough to prove absence because unsupported types can return the default: `GetValueNames` must omit the value. See [RegistryKey.GetValue](https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.registrykey.getvalue), `LockScreenGif/Services/Lockscreen/{LockscreenSettings,LockscreenPrerequisites}.cs` and `Tests/Prerequisites.Tests/Program.cs`.

Read-only prerequisite polling does not repair permissions. A successful apply repair/copy does not establish mode-switch values or a later polling result. "The lock-screen cache could not be checked" differs from a missing folder. Request the exact row detail and relevant missing evidence before prescribing another Picture selection or repair.

The loaded main page refreshes on same-session unlock, window activation (including native `WM_ACTIVATEAPP`) and five-second polling. Activation uses a separate event from diagnostic observations. `Prerequisite refresh requested: WindowActivated/SessionUnlock` logs only a request, not completion or resulting values. Overlap schedules a follow-up read; navigation/closure detaches handlers. Verify actual foreground HWND/process when reproducing missing activation, since visible z-order alone does not establish focus. See `LockScreenGif/Views/MainPage.Prerequisites.cs`, `LockScreenGif/Services/Diagnostics/WindowsSessionMonitor.cs` and `Tests/Prerequisites.Tests/SessionMonitorTests.cs`.

## Media selection, loading, and generation errors

A sanitized analytics issue names an operation boundary, not the exact failing API. Correlate the full event properties, operation ID, app version, and local logs. Separate GIF picker, file read, image decode, video metadata, frame indexing, Windows preview, frame extraction, GIF encoder, output-file opening, and generated-preview stages. A selected filename extension identifies the container label, not its codec or validity. Repeated occurrences from one installation can be retries; count attempts and affected installations separately.

| Signed HRESULT | Hex | Established meaning | What the code alone does not establish |
| --- | --- | --- | --- |
| `-2147467259` | `0x80004005` | `E_FAIL`: unspecified failure. | Which picker, storage, decoder, or playback call failed, or whether the source is corrupt. |
| `-1072868846` | `0xC00D5212` | `MF_E_TOPO_CODEC_NOT_FOUND`: Media Foundation could not find a suitable encoding/decoding transform. | The missing codec, whether audio or video caused the failure, or whether bundled FFmpeg can decode the source. |
| `-2147020345` | `0x800711C7` | `HRESULT_FROM_WIN32(4551)`, `ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION`: an Application Control policy blocked a file. | The blocked file, responsible policy, or whether its signature is missing, invalid, or disallowed. It is not evidence of disk exhaustion or corrupt source media. |

Microsoft sources: [common HRESULTs](https://learn.microsoft.com/en-us/windows/win32/seccrypto/common-hresult-values), [Media Foundation SDK definitions](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/Mferror.h), and [Win32 SDK error definitions](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/winerror.h).

Preserve the original HRESULT. For a `Win32Exception`, prefer its explicit `NativeErrorCode`, because its HRESULT may be generic. Only derive a Win32 code from an HRESULT with the `0x80070000` failure/facility prefix; taking the low 16 bits of every HRESULT would mislabel Media Foundation and COM errors. Older analytics reported `native code none` for an `IOException` carrying a Win32 HRESULT; this means the client did not extract the code, not that Windows supplied no useful status. Compare the event's app version before interpreting the older `io` or `native_failure` family.

The app uses distinct decoders: Windows APIs for metadata/playback, bundled FFmpeg for frame indexing/stills/export, and Gifski for GIF encoding. Failure of Windows metadata or playback does not establish that FFmpeg conversion is impossible. If a newer build recovers through FFmpeg metadata or allows editing with still previews, inspect the recovered exception and the actual terminal outcome together. A successful load with unavailable Windows playback is degraded success, not evidence that the original error disappeared or that continuous playback worked. Cancellation must remain cancellation and must not trigger recovery work. A successful metadata probe also does not prove the full frame scan or GIF encode will succeed.
### Hardware decoding evidence

A saved hardware preference, compiled FFmpeg backend or successful capability probe does not establish which decoder a particular video load used. Retain the actual indexing result and fallback flag. The opt-in `Tests/VideoIndexing.Tests --hardware --adapter N` suite requires at least one actual D3D11 result, but individual codecs and a supplied video can succeed through CPU fallback; inspect each result before claiming GPU coverage. `Tests/HardwareDecoding.Tests --probe` can successfully report Unavailable.

Do not assume an out-of-range `-hwaccel_device` number forces device initialization failure. FFmpeg and the driver can still produce D3D11 frames. Verify what the attempted decode actually returned. For a real unsupported-codec fallback control, use a small FFV1 clip, require a CPU result with the fallback flag, and compare its complete index with direct CPU decoding. Preserve failed test assumptions separately from application failures.

GPU indexing validation covers decoded presentation order and timing. Verify exported frames and native GIF generation separately; none of these measurements establishes visible lock-screen or sign-in playback.

For a policy block during GIF generation, use the generation stage before assigning a component. FFmpeg process creation and managed/native Gifski loading are separate possible boundaries; an encoder-stage failure is a lead, not proof that a particular DLL was blocked. Inspect the released package's signatures as well as source packaging rules: signing the installer or main executable alone does not establish that its managed and native dependencies are signed. A trusted signature can improve compatibility but cannot override an explicit organizational block. Do not suggest disabling Application Control or weakening permissions as the app fix.

When the affected user already has suitable local evidence, inspect matching `Microsoft-Windows-CodeIntegrity/Operational` block events and correlate event 3077 with its 3089 signature events by ActivityID. These identify the blocked binary, parent process, policy, and signature verification result; absence of an exported event is inconclusive. Keep paths, policy IDs, certificate details, raw messages, reports, and machine identifiers in private local evidence, never analytics or fixtures. See [Microsoft's Application Control troubleshooting guide](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/operations/appcontrol-debugging-and-troubleshooting).

Useful analytics remain bounded: operation/stage, numeric HRESULT/native code, known error family/component, metadata source, playback availability, and actual outcome. Do not add raw exception text, filenames, paths, or media to compensate for generic errors. Synthetic regressions should cover both raw Win32 and HRESULT forms, unrelated HRESULTs, stage propagation, recovered success, and cancellation without claiming to reproduce the affected Windows machine.

## Cache discovery and access

A `DirectoryNotFoundException` from `CacheLayout.ReadFolders` means a required component of `SystemData/<user SID>/ReadOnly` was missing during enumeration. This is consistent with an uninitialized per-user cache but does not establish why it was absent. A completed discovery with zero destinations is distinct. Neither reaches the copy loop; source verification is not destination verification.

Compare manual Picture selection/lock/unlock and a Windows API-on apply as separate initialization interventions. API-on can change the image/cache even if replacement later fails. Preserve a pre-intervention baseline when testing causality. A recovery sequence establishes that profile's outcome, not a universal fresh-profile requirement. If denied parent validation persists, do not repeat initialization advice as a demonstrated permission fix. Assess feature-related playback only after destination verification succeeds.

Collect paired normal/admin access reports for the same explicit target SID. Compare `Cache.RootValidation`, ancestor `Attributes`/`Acl`, and independent `Cache.FolderEnumeration`: a denied parent does not imply direct child enumeration failed. Read numeric ACL rights, inheritance and token details together; an allow entry or successful admin probe does not establish the normal client's post-repair access. Missing, denied, unexpected errors, skipped links and truncation remain distinct. See [the collection guide](../../../../docs/cache-access-report.md) and `Tests/LockscreenAccessReport.Tests.ps1`.

Current repair handles protected metadata and the fixed SID parent's metadata rights before scoped cache recovery. Unresolved attributes still fail closed (`PermissionRecoveryTests.StillDeniedAncestorFailsClosed`). Ordinary elevation, whole-app elevation, child-only grants and recursive SystemData grants are not substitutes for that validation. SYSTEM ownership does not date a permission change or prove every apply needs elevation. Compare older log stacks with their implementation rather than attributing current behavior retroactively.

Access reports retain `Error.NativeErrorCode`: native 5 distinguishes denied access from missing file/path 2/3 even with generic HRESULT `0x80004005`. Classification preserves the HRESULT and never parses localized text. Older missing native codes leave the individual ACL probe uncertain; independent attribute evidence can still identify denial. Private reports belong outside commits and analytics.

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

Both legacy `Trace draining exceeded five seconds.` and the current shutdown warning concern the helper's five-second consumer-worker wait after native stop. `TraceWorkerDrain` waits for processing, disposal and final correlation, then requests `StopProcessing` with a two-second grace. The warning alone identifies neither the delayed stage nor queue overflow. `DiagnosticProcessTrace` has a separate ten-second budget for final stop/IPC draining and different failure wording.

A timeout does not establish a large workload or transport overflow. Inspect the workload and shutdown measurements even when loss counters are zero. Windows documents that a real-time `ProcessTrace` call may take several seconds to return after the session stops; this is a possible contributor, not a report-proven cause. See [ProcessTrace](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-processtrace).

Check the latest `Operations.Timestamp` and `CompletedAt`, plus aggregate `LastAt`, against final snapshot `HashReadAt` for fresh hashes. If even the app's final inspection reads are absent from an otherwise untruncated trace, there is evidence that the report lacks activity near shutdown. A quiet interval by itself is not proof of a stall; omissions or unresolved correlation can also remove those records. `ProcessTrace.EndedAt` is assigned during worker cleanup and must not be treated as the last consumed event timestamp or a guarantee of full coverage. Zero loss counters do not establish successful draining.

Preserve the incomplete state and earlier positive evidence after a deadline; completed cleanup does not repair lost coverage. A longer timeout is not a demonstrated fix, and timeout alone proves neither failed nor successful animation.

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

For supplied Windows binaries, compare bytes/SHA-256, numeric versions, signatures and matching installed paths before disassembly. Identical `LogonUI.exe` bytes exclude that file difference, not dynamically loaded modules, configuration or graphics. Imports are not a loaded-module inventory. Do not call the cache technique patched from a version, frozen frame or absent GIF string. Keep binaries/inventories private.

Version strings can come from associated MUI resources; numeric `VS_FIXEDFILEINFO` and hashes distinguish identical bytes from a resource-string difference. See [GetFileVersionInfoEx](https://learn.microsoft.com/en-us/windows/win32/api/winver/nf-winver-getfileversioninfoexa). A MUI supplied on only one side is an unmatched input, not proven absence on the other. Verify installed resources and PE sections/imports before describing one as code-free.

For VirtualBox evidence, inspect graphics controller, acceleration, Guest Additions and display-driver logs read-only. Acceleration off plus display-only fallback is configuration evidence, not proof of an animation requirement. An authorized comparison must hold other variables constant, shut down normally before changing graphics, observe both lock/sign-in surfaces and retain a reboot-only control. Do not alter a VM to analyze a report. Oracle's [graphics documentation](https://docs.oracle.com/en/virtualization/virtualbox/7.2/user/guestadditions.html) does not guarantee lock-screen GIF playback.

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

## Native encoder DLL load failures

A present encoder can throw `DllNotFoundException` (`0x8007007E`) because a dependency is missing. The code alone does not identify that module. Preserve actual file hashes, PE architecture/imports and dependency inventory. A launcher, decoder or preview success does not establish native encoder loading.

Current source builds bundle an unmodified Microsoft x64 release `vcruntime140.dll` beside `Vendor/gifski/gifski.dll`, with `runtime-README.txt` and `runtime-provenance.json`; Gifski imports it. This app-local deployment does not require a separate system VC installation. Older released packages can omit the DLL, so identify the affected build and inspect its actual payload before assuming it is present. For such a package, an official matching [VC redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist/) is a separate environment remedy; it does not change the release payload. The .NET Desktop Runtime is a separate dependency. When updating the runtime, follow the notice/provenance and Microsoft's [redistribution guidance](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files?view=msvc-170); app-local copies are serviced through app releases.

Placement depends on the actual loader flags. `GifSkiService` uses `NativeLibrary.Load` on the absolute Gifski path. Windows .NET 10 uses `LOAD_WITH_ALTERED_SEARCH_PATH`, allowing dependencies beside that DLL; see the [CoreCLR implementation](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/vm/nativelibrary.cpp#L225-L242) and [Windows DLL search order](https://learn.microsoft.com/en-us/windows/win32/dlls/dynamic-link-library-search-order). Reassess placement if the API/flags change. Preserve the release architecture, unmodified publisher binary, signature, hash, imports and separate redistribution terms.

Inspect actual ZIP contents and built MSI payload, not just project declarations or success on a development machine with installed runtimes. `scripts/Test-NativeRuntimePackaging.ps1` and `Tests/NativeRuntimePackaging.Tests.ps1` cover publish/provenance and MSI project rules; embedded MSI bytes need separate inspection.

On an authorized disposable guest without a system VC runtime, reproduce the failure, add only the intended dependency, and check a fresh app process's loaded-module path/hash plus real generation/export with independently decoded frames. Keep app/native/media inputs unchanged and use fresh output directories. Repeated conversion/process exit and secure-screen playback need their own checks. Interpret `GetLastError` only after failure; a retained code after successful loading is not a failure. Do not switch codecs to address an established encoder-loader failure. Keep individual guest evidence and installers outside maintained docs/commits.

## Native encoder exits without a managed exception

For a test process that exits without a managed exception, the last `PASS` is not necessarily the failing operation. Locate the next `RUN` marker and preserve the child exit code in signed decimal and unsigned 32-bit hex; capture `$LASTEXITCODE` immediately. A workflow's own failure code is not the child's status. Missing exception text does not identify a native crash cause. Compare source, runner image and SDK, retaining crash dumps/full logs as private evidence.

Native module ownership differs from encoder ownership: `Gifski.Net` 1.2.0 `Dispose` frees its library reference, whereas `Finish` consumes the encoder handle. Native worker teardown can outlive completion; [Rayon pool disposal](https://raw.githubusercontent.com/rayon-rs/rayon/v1.10.0/rayon-core/src/thread_pool/mod.rs) signals [gradual termination](https://raw.githubusercontent.com/rayon-rs/rayon/v1.10.0/rayon-core/src/registry.rs), and [imagequant](https://raw.githubusercontent.com/ImageOptim/libimagequant/4.3.0/src/lib.rs) uses parallel iterators. This motivates retaining the module; it does not identify the cause of an unmeasured historical crash.

`GifSkiService` retains one successful native load for process lifetime; failed loads remain retryable. Each encoder still calls `Finish` exactly once, including submission failure. Never retry it after handle consumption or substitute module retention for encoder cleanup. `Tests/VideoEditing.Tests/MediaFailureTests.cs` checks residency after success/failure and exactly-once finalization without intentionally reproducing the unload race.

## Controlled Windows feature investigations

### Image feature compatibility evidence

New schema-2 reports optionally include `ApplyResult.WindowsImageFeatureAtApply`, a read-only configuration snapshot. GIF Apply does not change the feature, request feature elevation, or defer the diagnostic cycle because of its state. Each snapshot records its actual `FeatureId`, observation time, native query status, caller runtime state/priority, and independently read override existence/state/options/errors. The environment baseline and key-boundary observations also include a read-only description beside Animation effects under `Windows image feature`; the helper accepts the older `Windows image feature 38943831` key as fallback. Missing evidence in old reports is unknown, not a failed check.

Settings accepts a positive unsigned 32-bit feature ID and can reset the selection to default 38943831. `ChangeWindowsImageFeatureId` and `ResetWindowsImageFeatureId` record old/new selections in action detail. These preference changes do not alter the old or new Windows feature state; explicit enable/disable buttons operate on the ID selected for that operation. Compare historical snapshot/result IDs before relating actions to the current selection. Windows updates may retire or replace IDs; a valid numeric ID or successful native readback does not establish its lock-screen purpose. Never rewrite historical evidence using the current preference.

Explicit prerequisite and Settings buttons produce `PrerequisiteActions` with UTC timestamp, action, outcome, detail and optional `WindowsImageFeature` result. The last 100 actions from the current app lifetime are attached when a diagnostic snapshot or export is requested, including actions before the test and after it finishes. They are action history, not per-test mutation evidence or a complete lifetime audit. Feature action results include desired state, outcome, native-set status, change attempt, observed runtime/overall change, unknown-outcome flag, errors and `Before`/`After` snapshots. The Python helper separates `AtApply`, `Actions` and `LegacyApplyAction`; it preserves old fields without deriving new requirements from them.

Default feature 38943831's compatibility target is **Disabled (1)**. **Enabled (2)** was associated with static GIF playback in the controlled investigation, not proven across all Windows configurations. Explicit actions use the native runtime setter and persist the selected ID under machine priority 8/options 0; the default encoded leaf is `2920700556`, while other IDs differ. Registry-and-restart observations do not validate immediate playback after the runtime setter or a custom ID's role. The app imposes no restart requirement. Native success, caller state and stored configuration never establish LogonUI's effective branch or animation.

Interpret `ChangeAttempted`, `Changed` and `ChangeOutcomeUnknown` separately. A partial write may set `Changed=true` even when the operation fails. Lost helper responses or unreadable final state set `ChangeOutcomeUnknown=true`; `Changed=false` then does not prove no mutation. Keep the final readback and errors together. Do not replace an unknown outcome with a claimed rollback or successful fix.

A failed or uncertain explicit action needs re-checking and completion through the feature button. Applying the GIF will not repair that action. Native-set success, runtime readback and persisted override verification are separate evidence; preserve any partial state or failure instead of labeling the entire action successful.

Record Animation effects independently of feature state. Earlier controlled observations measured a crossover with effects On; that bounds those observations rather than establishing a universal requirement. A visual toggle can disagree with persisted calling-user readbacks, and neither identifies the secure-screen renderer's effective state. Preserve the actual readbacks and observed clock/sign-in playback before attributing static or black backgrounds to that preference. Re-checking the setting is a troubleshooting step, not proof of a cause.

Enabling the feature from Settings writes Enabled; it is not restoration of historical registry absence or an ownership-based rollback. The UI warns that enabling can stop GIF animation. Neither Apply nor Remove resets this setting. See [implementation and sources](../../../../docs/windows-image-feature.md).

Record DisplayVersion, build/UBR and package identity separately. A same-build working machine constrains a version-only explanation; distinguish reported playback from independently captured pixels. Keep stored configuration, a diagnostic caller's API result, sampled consumer data, static code and observed playback as separate evidence. Tool-derived feature layouts and encoded registry names are not supported Windows contracts.

A controlled registry intervention needs exact prior presence/value kinds, matched media/cache/hardware/user controls, fresh readback after each action/restart, and rollback/reapplication with actual clock/sign-in captures. Record whether a restart occurred. Restore prior absence rather than inventing Enabled. Unexpected values require preserved evidence before cleanup; any narrow removal must match the owned creation receipt and current contents. Reapplication creates new ownership evidence, so an old receipt cannot authorize removing it.

Distinguish managed interop failure from a native return. An argument-binding exception can occur before Windows is called; inspect actual managed types and use a correctly typed retained SafeHandle owner. Command failure alone proves neither native failure nor unchanged state. Independently inspect post-state and preserve partial mutations.

Transport timeout and native query/publisher outcome are separate. Join retained local rows, source, publication hashes and terminal outcomes; a completed result can survive a transport failure, and an unexecuted next operation is not a failed query.

Verify capture liveness within the actual phase. A byte-identical first-boot feed without a live control is inconclusive. Keep first boot, post-login and recovery captures distinct. Preserve a conservative classifier's result alongside direct semantic evidence: a recognized clock-minute change can establish a live display while a classifier requiring several control transitions remains inconclusive. Inspect partial-update frames without trimming them into a cleaner sequence.

If a one-template capture has changing control pixels but an inconclusive classifier result, inspect frame deltas and phases before changing thresholds. Fades, control disappearance and GIF movement inside a control ROI are not independent caret blinks. Require repeated changes while the recognizable control remains on the same surface. Preserve full input/classification; label any supplemental settled-frame observation by exact frame range, hashes, duration and actual transitions. Prefer a fresh settled-phase capture.

Measure immediate feature-action playback, a subsequent GIF reapply, and post-restart playback separately. A successful native setter, caller runtime readback, and persisted override verification do not establish that an existing secure-screen consumer has adopted the new behavior. Recovery observed after restarting narrows the result to that measured sequence; it does not prove every affected machine requires a restart or identify the consumer's refresh mechanism. Keep media, fresh cache hashes, Animation effects, and the actual clock/sign-in surfaces comparable across those observations.

Interpret fixed consumer-data samples only through widths and expressions verified in the exact matching code. A sequential cache snapshot does not prove an invocation, executed branch, loaded-code equality, image ownership or atomic decision. Static branch evidence may explain a reproducible workaround without establishing a universal Windows defect.

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
