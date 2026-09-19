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

## Code and verification map

Paths below are relative to the repository root.

| Concern | Code | Isolated test project |
| --- | --- | --- |
| Commit and verification timestamps | LockScreenGif/Services/Lockscreen/VerifiedCacheWriter.cs | Tests/ApplyPipeline.Tests |
| Read findings and icons | LockScreenGif/Services/Diagnostics/DiagnosticImageReadFinding.cs, DiagnosticTraceFindings.cs; LockScreenGif/ViewModels/DiagnosticFindingViewModel.cs | Tests/Diagnostics.Tests |
| Baseline and final hashes | LockScreenGif/Services/Diagnostics/CacheFileReader.cs, CacheCollector.cs | Tests/Diagnostics.Tests |
| Polling, bounded details and shutdown | LockScreenGif/Services/Diagnostics/DiagnosticProcessTrace.cs, DiagnosticRun.cs | Tests/Session.Tests |
| ETW correlation, aggregates and ownership | LockscreenGif.Privileged.Helper/Tracing/ | Tests/ProcessTracing.Tests |
| Report schema and redaction | LockscreenGif.Privileged.Contracts/TraceEvidence.cs; LockScreenGif/Services/Diagnostics/DiagnosticReportWriter.cs, DiagnosticRedactor.cs | Tests/Diagnostics.Tests |

Run an isolated project with `dotnet run --project Tests/<project>/<project>.csproj -c Release`. Build the WinUI app with `dotnet build LockScreenGif/LockscreenGif.csproj -c Debug -p:Platform=x64`. The opt-in native harness and real-machine acceptance procedure are in `docs/diagnostics.md`; do not silently trigger elevation/locking when asked only to analyze a report.

Authoritative semantics: [FileIo_ReadWrite](https://learn.microsoft.com/en-us/windows/win32/etw/fileio-readwrite), [FileIo_OpEnd](https://learn.microsoft.com/en-us/windows/win32/etw/fileio-opend), [file caching](https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching), [ProcessTrace shutdown](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-processtrace).
