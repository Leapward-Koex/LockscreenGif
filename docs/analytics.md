# Usage analytics

The app sends a small set of explicit events to PostHog's EU capture endpoint.
The project tokens and host are in `LockScreenGif/appsettings.json`; no personal
API key, Firebase configuration, browser, or backend is required. The project
token is a public ingestion credential, not an account-management credential.

## Settings and first launch

On the first launch with analytics configured, sharing is enabled and a notice
explains how to opt out. Its **Open settings** button opens the **Settings** tab.
**Settings > Analytics > Share usage analytics** saves the choice immediately.
An existing disabled preference is never replaced by the first-launch default.

The choice and a random installation ID are stored in
`%LOCALAPPDATA%/LockscreenGif/analytics.json`. An enabled-to-disabled transition
captures one final `analytics_opted_out` event before removing the local identifier.
Opt-out discards other queued events and cancels their active delivery. The final
event uses the outgoing random installation/session IDs and common metadata only;
it is attempted asynchronously with a three-second deadline from capture, no
retry, and no disk storage. Disabling never waits for delivery. A stalled worker,
offline connection, or immediate shutdown can lose the event. Repeating opt-out
or starting with sharing disabled emits nothing. It measures the runtime opt-out
action, not successful preference persistence or a complete count of all opt-outs.
Re-enabling generates
a new identifier. A request already in flight may still arrive after cancellation;
packets already sent cannot be retracted. Already delivered events are not deleted. If saving fails,
sharing stops for the current process and the Settings page reports that the
choice could not be saved. Unreadable or malformed settings leave sharing off.

Builds compiled by GitHub Actions use `ProductionProjectToken`. All other
builds, including local Debug and Release, use `DevelopmentProjectToken`.
MSBuild embeds a `GitHubActionsBuild` assembly marker when `GITHUB_ACTIONS=true`
at compile time; the app reads that marker, never the runtime environment.
Copying a GitHub-built executable to a local PC therefore keeps production
routing. Missing development configuration never falls back to production.
Both environments respect the same saved opt-out preference. Events include
an `environment` property (`development` or `production`). Automated tests only
use synthetic tokens and fake HTTP transports.

## Events

| Event | Meaning |
| --- | --- |
| `app_opened` | App activation completed, with the Windows compatibility fields listed below when available. |
| `analytics_opted_out` | Final best-effort event captured when enabled sharing is turned off; only the preference transition can emit it. |
| `page_viewed` | Lockscreen, Diagnostics, or Settings was opened. |
| `gif_selected` | GIF selection and preview initialization completed, failed, or were cancelled. Success does not prove every frame decoded or Windows lock-screen playback. |
| `video_load_started` | A selected video began indexing and preview initialization. |
| `video_load_completed` | Video loading completed, failed, or was cancelled. Success means frame editing is available; `playback_available=false` identifies a load that succeeded without Windows video playback. |
| `gif_generation_started` | Conversion began with the requested clip and output settings. |
| `gif_generation_completed` | GIF generation completed, failed, or was cancelled. |
| `gif_save_completed` | Saving and file-provider completion finished, failed, or were cancelled. |
| `lockscreen_apply_started` | A lockscreen or diagnostic apply was requested. |
| `lockscreen_apply_completed` | Apply finished with success, partial failure, failure, or cancellation. |
| `lockscreen_removal_completed` | Removal from Settings succeeded, partially failed, failed, or found nothing to remove. |
| `diagnostic_test_requested` | A diagnostic test was requested; not confirmation that the test completed. |
| `diagnostic_stop_requested` | The user requested that the diagnostic test stop. |
| `diagnostic_report_export_completed` | A diagnostic report export succeeded, failed, or was cancelled; report contents are not sent. |
| `app_error` | Best-effort structured error details before existing fatal-error handling. |
| `$exception` | PostHog Error Tracking event for an actual caught or unhandled error. Operation failures keep their existing terminal event and send this companion event with the same available operation/source context. Handled cancellations are excluded. |
| `log_export_completed` | Saving a ZIP from Settings succeeded, failed, or was cancelled. Only outcome, elapsed time, and structured error details are sent; no log contents, names, or destination paths. |

Each started operation has a random `operation_id`, repeated on its terminal
event. It identifies one attempt, not a media file or a whole editing session.
The Choose/Edit/Set/Done stages do not send additional page or navigation events.
Continue from Edit emits generation events only when conversion actually runs;
returning to an unchanged prepared GIF does not start another attempt. Set and
Retry retain normal `workflow=lockscreen` apply events. The completion screen
does not emit a separate success or playback event.
Video picker cancellations occur before loading starts and therefore can have
a terminal event without an operation ID or a matching start. A missing terminal
event does not prove a crash: delivery is best effort.

Properties use a fixed typed allowlist:

| Properties | Meaning |
| --- | --- |
| `outcome`, `page`, `error_kind` | Bounded categories for results, navigation, and errors. |
| `exception_type`, `error_hresult` | Allowlisted exception family and signed 32-bit HRESULT on exception failures. Unknown types use `other`; runtime type names and exception text are never sent. |
| `error_context` | On `$exception`, the fixed error boundary, such as `gif_generation`, `video_load`, `lockscreen_apply`, `diagnostic_run`, or `app_crash`. |
| `error_component`, `native_error_code` | `ffmpeg` or `gifski` and their numeric exit/return code for structured media failures. Win32 exceptions and failed `HRESULT_FROM_WIN32` values also supply their numeric Win32 code, without implying a media component. Other HRESULT facilities retain only `error_hresult`. |
| `operation_id`, `workflow` | Attempt correlation and `lockscreen` versus `diagnostics` apply context. |
| `lockscreen_source` | Origin of the GIF on both `lockscreen_apply_started` and `lockscreen_apply_completed`: `video` (generated in the app), `user_gif` (picked by the user), or `bundled_gif` (the diagnostic reference animation). `unknown` is used when an internal caller cannot supply provenance. |
| `duration_ms` | Elapsed operation time, excluding the file picker where applicable. |
| `requested_width`, `requested_fps` | Requested generation settings; FPS `0` means all source frames. These are not measurements of validated output media. |
| `requested_fps_mode`, `source_fps` | Mode is `all_source_frames` for requested FPS `0`, otherwise `target`. Source FPS is nominal video metadata when known, not measured output FPS. Zero remains in `requested_fps` for compatibility. |
| `clip_duration_seconds`, `selected_frame_count` | The requested conversion range. |
| `extracted_frame_count` | Frames reported by FFmpeg after successful extraction. May be fewer than selected source frames when a target FPS is used; omitted if extraction fails. |
| `media_load_stage` | Last observed GIF selection or video load stage: `picking_file`, `reading_metadata`, `indexing_frames`, `opening_preview`, `opening_file`, `decoding_image`, `completing`, `playing_preview`, or `reading_fallback_metadata`. Retained on terminal events and companion errors when available. |
| `metadata_fallback_used`, `playback_available` | Optional video load observations: whether the bundled FFmpeg metadata reader was attempted, and whether Windows video playback initialized. `false` is an observed result; an absent value means not yet known. Runtime playback failures report `playback_available=false`. These do not certify successful GIF generation or lockscreen playback. |
| `failure_stage`, `failure_stage_duration_ms` | Generation stage where an exception was caught, and elapsed time within that stage. Stages are `preparing`, `extracting_frames`, `encoding_gif`, `opening_output`, `loading_preview`, and `completing`. |
| `extraction_duration_ms`, `encoding_duration_ms`, `preview_duration_ms` | Elapsed times for completed generation stages, retained if a later stage fails. A missing value means the stage did not complete, not zero time. |
| `uses_reference_gif` | Whether a diagnostic test requested the bundled reference GIF. |
| `gif_size_bytes`, `gif_width`, `gif_height` | Source GIF file size in bytes and logical-screen width/height in pixels on `lockscreen_apply_completed`, for normal and diagnostic applies. These describe one source file, not total cache storage or monitor resolution. |
| `target_count`, `copied_count`, `verified_count`, `failed_count` | Cache operation counts. Cancelled applies omit `failed_count` because some destinations may not have been attempted. |
| `api_requested`, `api_completed` | Whether the Windows lockscreen API was requested and completed. |
| `apply_failure_reason` | Typed failure category on failed or partial `lockscreen_apply_completed` events and their companion `$exception`, when present. Values are `source_read_failed`, `invalid_source`, `windows_api_failed`, `cache_inaccessible`, `cache_missing`, `no_destinations`, `cache_discovery_failed`, `copy_failed`, `verification_failed`, and `unknown`. Success and cancellation omit this field. |

Apply failure reasons describe the stage and evidence available to the app.
`cache_inaccessible` means discovery or pre-copy cache access was denied, including
when permission repair was rejected. `cache_missing` means a required
cache directory was absent. `no_destinations` means cache discovery completed
without finding a supported destination. These can all have `target_count=0`
and `failed_count=0`: file failure counts cover discovered targets, not errors
that prevent discovery. Use `outcome` to identify a failed operation. A completed
Windows API call does not establish that the animated cache apply succeeded.

Other discovery errors use `cache_discovery_failed`; uncategorized results or
exceptions outside a known apply stage use `unknown`. A reason is never inferred
from a raw exception message, filename, or path. Missing reasons on older events
mean unrecorded, not a successful operation. These categories explain cache
writes and verification, not whether Windows displayed the animation.
For partial results, the first unsuccessful destination supplies the reason;
failures after a committed copy use `verification_failed`. Detailed exception
families and codes remain available on the companion error event.

Common properties are app version, Windows version, platform, environment,
random installation ID, and `$session_id`. No paths, filenames, media, file hashes,
usernames, Windows SIDs, exception messages, stack traces, or diagnostic reports
are sent.

### PostHog Error Tracking

The integration follows PostHog's [manual Error Tracking contract](https://posthog.com/docs/error-tracking/installation/manual).
`AnalyticsService.TrackFailure(event, exception, properties)` keeps the existing
operation result and reports its exception. `CaptureException(exception, context,
properties)` reports caught errors without creating a new product-usage event.
Both use the same saved consent, environment, identity, bounded queue, and HTTP
worker as usage analytics. Neither waits for HTTP delivery.

Each `$exception` contains:

- `$exception_list`: one object with an allowlisted exception `type`, a `value`
  assembled from fixed context/error categories and numeric codes, and
  `mechanism: { handled, synthetic: false, type: "manual" }`. Catch sites use
  `handled=true`; the existing fatal handler uses `false`.
- `$exception_fingerprint`: a stable SHA-256 of the approved boundary, exception
  family, category, generation stage, HRESULT, native component and native code.
  A known media load stage also distinguishes failures; missing or invalid stages
  are excluded from the fingerprint. Recovery flags do not split issues.
  Operation IDs, source selections, durations, app versions and installation IDs
  do not split an issue into new fingerprints.
- The available typed operation ID, workflow, source, timing and error properties
  already used on the corresponding terminal event.

Raw exception messages, `Data`, runtime type names, source files, function names
and stack traces are not sent. PostHog accepts exceptions without a stacktrace;
these issues show the approved context and error codes rather than a source-code
stack view. Fingerprints group this sanitized information, never hashes of private
messages or paths. Unknown exception families use `Exception` in the PostHog
list and `other` in the existing `exception_type` property.

The same exception object (including known single-cause wrappers) is reported
once per analytics-service lifetime, so an inner operation catch and outer UI
fallback do not duplicate it. Weak keys do not retain exceptions indefinitely,
and queued events contain only typed snapshots. Different exception instances
still count as separate occurrences. A dropped capture is not retried simply
because the same exception reaches another boundary.

Covered boundaries include GIF selection/generation/save, video load and preview,
log/report export, lockscreen apply/removal, main-action and diagnostics UI
fallbacks, log-folder opening, shutdown failures, actual diagnostic run/trace
failures, lockscreen verification failures, and the fatal handler. Still-preview and
thumbnail failures are each limited to one report per successfully loaded video;
Windows playback failure is recorded separately before releasing the failed player,
so an earlier still-preview error cannot hide a loss of playback.
Trace failures are limited per trace lifetime. Expected cancellation, diagnostic
observations, successful permission retries, ordinary polling/probe misses and
analytics' own failures do not create issues.

Apply and removal internals sometimes return failure results instead of throwing.
They retain the first actual exception in a transient `[JsonIgnore]` field
and report it once at the operation boundary. Partial failures keep `outcome=partial`.
The known committed-file verification mismatch receives a fixed local exception;
arbitrary result `Error` strings are never parsed or transmitted. These transient
exception fields are excluded from diagnostic report JSON.

In PostHog, open **Error Tracking** and filter by `environment`, `app_version`,
`error_context`, and `workflow`. `$exception` also appears in the activity feed.
Use `operation_id` to join it to a generation/apply result and `lockscreen_source`
for source breakdowns. Existing custom events are retained for funnels; historical
failures are not converted into Error Tracking issues.

Regression tests validate this capture payload with fake HTTP transports and do
not send live exceptions. After deploying a build, confirm a real failure's
`$exception` in the correct project's activity feed and Error Tracking view.
Delivery remains best effort: opt-out, offline connections, queue pressure or
immediate process termination may prevent an error from arriving. Error reporting
does not delay fatal shutdown to force delivery.

### Investigating GIF selection and video load failures

Break down failed `gif_selected` and `video_load_completed` events by
`media_load_stage`, then `error_kind` and `error_hresult`. Join their companion
`$exception` by `operation_id` when supplied. A generic `E_FAIL` alone does not
identify a corrupt file, missing codec, picker failure, or an app bug; the stage
narrows which operation failed without collecting file names or exception text.
Older events without a stage cannot be diagnosed retroactively.

| Evidence | Interpretation |
| --- | --- |
| `error_hresult=-2147467259` (`0x80004005`) | `E_FAIL`, an unspecified native failure. Keep `native_failure`; do not infer a particular codec or security product. |
| `error_hresult=-1072868846` (`0xC00D5212`) | `MF_E_TOPO_CODEC_NOT_FOUND`, classified as `codec_missing`. Windows could not find a compatible encode/decode transform; this does not identify the missing codec. |
| `error_hresult=-2147020345` (`0x800711C7`) or `native_error_code=4551` | `ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION`, classified as `security_policy_blocked`. Windows Application Control blocked a file; the error alone does not identify which file or policy. |
| Win32 `1260` or `577`, including their `HRESULT_FROM_WIN32` forms | `security_policy_blocked`: group-policy blocking or failed digital-signature verification respectively. These need trusted installation/policy review, not decoder retries. |

The numeric definitions come from Microsoft's
[Windows SDK error constants](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/winerror.h),
[Media Foundation error constants](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/Mferror.h),
[group-policy error reference](https://learn.microsoft.com/windows/win32/debug/system-error-codes--1000-1299-),
and [signature-verification error reference](https://learn.microsoft.com/windows/win32/debug/system-error-codes--500-999-).
Only the Win32 facility is decoded into `native_error_code`; `E_FAIL` and Media
Foundation codes remain full HRESULTs. Raw messages, codec names, paths, and
security-product names are not inspected or transmitted. Better classification
and newly recorded stages can create new issue fingerprints after an update;
compare error codes and app versions when relating them to historical issues.

Use `metadata_fallback_used` and `playback_available` alongside the actual load
outcome to distinguish recovered loads from failed attempts. A successful load
with `playback_available=false` still needs separate generation and apply outcomes;
it is not proof that conversion or Windows lockscreen playback succeeded.
Windows metadata failure can produce a handled `$exception` followed by a successful
load using the bundled FFmpeg reader. Windows preview failure can likewise produce
a handled `$exception` while frame preview, trimming and conversion remain available.
Count terminal `video_load_completed` outcomes when measuring unusable video loads;
counting all handled exceptions would also include these recovered attempts.

### Investigating generation failures

Break down `gif_generation_completed` with `outcome=failed` by `failure_stage`,
then `error_kind`, `exception_type`, `error_component`, and `native_error_code`
or `error_hresult`. The same safe error details are used by other exception-based
operation events. Result-based failures without an exception can still have only
an outcome or coarse category. The fixed error families include cancellation,
permission denied, invalid media, I/O, timeout, out of memory, disk full, missing
or incompatible dependencies, native failure, invalid state, invalid argument,
missing codecs, security-policy blocks, and other. Classification uses exception types and numeric codes; it never
parses error messages. Known single-cause exception wrappers are unwrapped up
to eight levels; multiple-cause aggregates remain aggregates.

For native media failures, use the component with its code: FFmpeg's exit code
and Gifski's return code have different meanings. Gifski output setup, frame
submission, and finalization results are checked. Its known invalid-state,
permission, invalid-input, and timeout results receive more specific categories;
other results remain `native_failure` with the original numeric code. The
HRESULT on a media wrapper describes the managed exception, so the native code
is the useful discriminator. FFmpeg exit codes may still be too coarse to name
the exact cause; detailed stderr remains in local logs only.

`loading_preview` means encoding and reopening the output already completed;
an error there is a preview/decode failure, not evidence that the encoder failed.
Stage durations separate slow extraction from slow encoding or preview loading.
They add no per-frame events or network work to conversion. Superseded generation
attempts that finish conversion emit a cancelled terminal event.

Use `requested_fps_mode` to distinguish all-source-frame conversions from target
rates. For variable-frame-rate sources, neither nominal `source_fps` nor
`selected_frame_count / clip_duration_seconds` proves a constant output frame
rate. Older events lack the new details and cannot be diagnosed retroactively.

Apply size metadata comes from the same held source stream and header used by
the apply operation; it adds no separate file open or image decode. It is also
included when a later apply step fails, if the source metadata was available.
Unknown sizes/dimensions are omitted, and the start event has no size fields
because it precedes opening the source. GIF dimensions describe the header's
logical screen, not a full decoding or playback validation.

Apply source is captured together with the selected file, then copied into the
attempt's start and terminal events with the same `operation_id`. It remains
the same for success, partial failure, failure, cancellation, and exceptions,
even if the current selection changes while the operation is waiting or running.
Retries keep the prepared GIF's source. Diagnostics using the current GIF retain
its video/user-GIF origin; diagnostics using the reference animation report
`bundled_gif`. Keep `workflow=lockscreen` when measuring normal usage.
Source describes how the app received this selection: a previously generated
GIF saved to disk and later picked again is `user_gif`. No file name, path,
content inspection, or file hash is used to infer or transmit this category.

### Windows compatibility on `app_opened`

The launch event also includes the following local settings to help investigate
edition, language, encoding, and architecture differences:

| Property | Meaning |
| --- | --- |
| `windows_edition` | A stable edition category such as `home`, `pro`, `pro_n`, `education`, or `enterprise_ltsc`. N and single-language editions remain distinct. Unmapped SKUs use `unknown`. |
| `windows_product_sku` | Windows' numeric product-type enumeration, not a license key or device identifier. Preserved for newer or uncommon editions. |
| `windows_release` | Release label, such as `24H2`, if available. Only four-digit legacy labels and `YYH1`/`YYH2` labels are accepted. |
| `windows_build`, `windows_update_revision` | OS build and servicing revision (`UBR`), for example `26100` and its update revision. The existing `windows_version` remains on all events. |
| `os_architecture`, `process_architecture` | Separate `x86`, `x64`, `arm`, or `arm64` values; an x64 app can run on an ARM64 OS. |
| `windows_user_locale` | The Windows user's regional-format locale, such as `en-NZ`. |
| `windows_system_locale` | The system default locale, relevant to legacy non-Unicode applications. It can differ from the user's locale. |
| `windows_display_language` | First entry in Windows' ordered preferred display-language list. |
| `windows_install_language` | System default UI/install language; may differ from the current display language. |
| `is_elevated` | Whether the current app token has administrator privileges. No account or group names are sent. |
| `is_remote_session` | Whether Windows reports the current session as a remote session through `SM_REMOTESESSION`; this is a compatibility indicator, not a complete remote-access detector. |

Locale/language values are canonical culture tags from the runtime's standard
culture list. Custom, unsupported, missing, or invalid values are omitted. The
collector reads only these fixed settings, never the full registry, a language
inventory, diagnostic reports, registered owner, product key, or machine identity.
Edition comes from the numeric
[GetProductInfo API](https://learn.microsoft.com/windows/win32/api/sysinfoapi/nf-sysinfoapi-getproductinfo),
not a localized Windows product-name string. Display language uses
[GetUserPreferredUILanguages](https://learn.microsoft.com/windows/win32/api/winnls/nf-winnls-getuserpreferreduilanguages)
so language packs can supply names rather than ambiguous language IDs.

Collection happens on the existing analytics worker only for enabled launch
events. Each native/registry read fails independently; unavailable metadata does
not prevent the base launch event from being sent. No commands, WMI queries,
network lookups, or permission prompts are used. Opt-out is checked again before
sending, and even a stalled collector cannot delay app actions or shutdown.

In PostHog, select `app_opened` and use these properties as filters or breakdowns.
They are event properties, not person properties. They are not automatically
attached to later operation events: correlating failures with launch context
requires a SQL insight joining to the latest preceding launch for the same
installation. Settings describe launch-time collection, not a continuous monitor.

Sessions begin on the first captured event after launch. The next captured
event starts a new session after 30 minutes without captured activity, after
24 hours, or after the system clock moves backwards. Session IDs are UUIDv7
values created no later than their first event. No timer or heartbeat is needed
to keep a session alive. These boundaries follow PostHog's
[session conventions and custom ID requirements](https://posthog.com/docs/data/sessions).

**Apply success only means the app completed its cache operation.** It does not
prove that Windows displayed an animation. Diagnostic applies use the same
central hook with `workflow=diagnostics`; filter them out when measuring normal
lockscreen usage. An outcome of `cancelled` should be excluded from failure-rate
calculations; `partial` counts as a failure. Removal is not a claim that an
original wallpaper was restored.

## Delivery and PostHog setup

Delivery happens in the background with a queue of at most 64 pending events.
Capture, HTTP serialization, and network work are separated so transport work
cannot hold the state lock needed by app operations. Capture drops an event
instead of waiting for a busy state lock. Offline or blocked connections,
rejected requests, timeouts, and queue overflow can all lose events. Requests
have a three-second cancellation deadline. There is no disk spool, retry loop,
or heartbeat.

Normal close and fatal-error handling call the nonblocking `Stop()` method:
pending events are discarded and cancellation is requested without waiting for
network delivery or transport callbacks. Consequently an operation completion
or crash event immediately before exit might never arrive. Analytics does not
delay closing or media/lockscreen work for a network response. Counts, funnels,
and crash figures are best-effort observations, not a complete audit trail.

Each queued event has a fixed top-level `uuid` and timestamp. PostHog can
eventually deduplicate identical UUID/event/timestamp/distinct-ID combinations;
any future retry implementation must preserve all four values. See
[event deduplication](https://posthog.com/docs/data/events#event-deduplication).

Person-profile creation and GeoIP enrichment are disabled in event properties.
PostHog still receives the network connection's source IP. In the PostHog project,
set **Settings > Project > IP data capture configuration > Discard client IP data**
if you also want to prevent storing that address. This is separate from GeoIP.
See the [capture API](https://posthog.com/docs/api/capture) and
[data storage privacy documentation](https://posthog.com/docs/privacy/data-storage).

Use PostHog's activity/live-events view in the development project to inspect
events from local builds, or the production project for GitHub-built releases.

## Dashboard recipes

These are instructions for creating insights; this repository does not provision
PostHog dashboards. Use the production project for product decisions and keep
`environment=production` as a dashboard filter. Use event-property filters,
because person profiles are disabled.

PostHog's "unique users" represent **installations** here, not verified people.
One person can have several PCs, and deleting preferences or opting out and
back in creates a new identity. Disabled collection and lost events are absent
from all counts. Existing wallpaper use after the app closes is not observable.

Create an action named **Meaningful app use** that matches any of these events
with `outcome=succeeded`: `video_load_completed`, `gif_generation_completed`,
`gif_save_completed`, or `lockscreen_apply_completed` with `workflow=lockscreen`.
Use one OR action for unique-installation statistics: adding the unique counts
for individual events would count the same installation several times.

| Question | Insight setup |
| --- | --- |
| How many installations open the app? | Trends: `app_opened`, Unique users, daily interval. Keep total event count separately to measure launches. |
| How many installations actually use its features? | Trends: **Meaningful app use**, Unique users per day (DAU), plus WAU and MAU. Compare these with launch counts. |
| How many distinct usage sessions occur? | Trends: **Meaningful app use**, Unique sessions. This counts observed feature sessions, not foreground minutes. |
| Which features are used? | Trends: successful feature events individually, Unique users; `page_viewed` broken down by `page` for navigation. |
| Where do workflows stop? | Separate sequential funnels: successful `gif_selected` to successful normal apply; and successful video load to generation to normal apply. Start with a one-hour conversion window and inspect time-to-convert. A funnel joins observed user events and does not prove the same media file was used. |
| Which attempts lack a completion? | Pair `video_load_started`, `gif_generation_started`, or `lockscreen_apply_started` with the corresponding terminal event by `operation_id` in a SQL insight. Do not join IDs across different operation types. Missing completion means unobserved, not necessarily failed. |
| How often does normal apply succeed? | Count `lockscreen_apply_completed` with `workflow=lockscreen`. Use `succeeded / (succeeded + partial + failed)`; exclude cancellations. Show the denominator and break down by `app_version` and `windows_version`. |
| Does apply reliability differ by source? | Break the same normal-apply success rate down by `lockscreen_source` (`video` versus `user_gif`). Use `workflow=diagnostics` separately for bundled-reference comparisons. Join start/completion by `operation_id`; missing source on older events means unrecorded, not `user_gif`. |
| Why do applies fail before finding targets? | Filter `lockscreen_apply_completed` to `outcome=failed` or `partial`, break down by `apply_failure_reason`, and inspect `target_count=0` separately. Distinguish `cache_inaccessible`, `cache_missing`, and `no_destinations`; compare app/Windows versions and API options. Keep `workflow=diagnostics` separate from `workflow=lockscreen`, exclude cancellations, and treat missing reasons as unrecorded. Companion `$exception` events share the operation ID and reason when an exception is available. |
| What makes operations slow? | Median and p95 of `duration_ms` for successful generation/apply. Compare generation's completed extraction, encoding, and preview durations. Break down by requested width, FPS mode, source/target FPS, and clip duration; show sample counts. |
| How long does selecting a video take? | Median and p95 of `duration_ms` on successful `video_load_completed`, compared by `app_version`, `metadata_fallback_used` and `playback_available`; show sample counts. This measures preparation until frame editing is available. The first exact still and timeline thumbnails load afterward, so their readiness is not included. |
| Where does GIF generation fail? | Filter `gif_generation_completed` to `outcome=failed`, break down by `failure_stage`, then `error_kind` and native component/code or exception family/HRESULT. Compare versions and requested settings; exclude cancellations. Missing fields on older events mean unknown, not success. |
| Do installations return? | Retention: successful normal apply as the start event and **Meaningful app use** as the return action, weekly periods across eight weeks. Use **On or after** for this occasional-use utility; distinguish first-use and recurring retention. |

Trends supports distinct users/sessions, rolling seven-day WAU, rolling
30-day MAU, and numeric percentiles. See
[aggregation options](https://posthog.com/docs/product-analytics/trends/aggregations).
Funnels support ordered steps and event-property filters; see
[funnel configuration](https://posthog.com/docs/product-analytics/funnels).
Retention measures later observed use and can include returns after the chosen
period; see [retention options](https://posthog.com/docs/product-analytics/retention).
Low repeat use alone is not a reliability problem for an app whose wallpaper
can keep working without reopening it.

Personless events support trends, funnels, and most insights, including
event-based retention. They do not support Lifecycle insights, person-property
filters, saved person cohorts, or group analytics. Do not use Lifecycle's
new/returning/dormant categories for this integration. See
[anonymous-event capabilities](https://posthog.com/docs/data/anonymous-vs-identified-events).

## New feature checklist

- Define the product question before adding events. Prefer a meaningful action
  or operation result over every click, slider movement, frame, or polling tick.
- Reuse established names and outcome categories. For a long operation, give
  its start and exactly one terminal result the same random `operation_id`;
  distinguish cancellation from failure and describe partial success precisely.
- Add only typed, allowlisted properties to `AnalyticsProperties` and the
  capture mapping. Bound numerical values and enums. Never pass paths, media,
  arbitrary strings, exception text, or diagnostic objects.
- Include workflow context where different screens share an operation. Keep
  requested configuration distinct from verified output or user-observed success.
- Preserve opt-out and environment routing. Tracking must be optional, must not
  make app behavior depend on delivery, and must not introduce network waits on
  UI, operation, or shutdown paths.
- Add focused fake-transport regression checks for new schema/outcome behavior
  and relevant failure paths. Update the event table and dashboard recipe when
  the new event changes what can be measured.

## Offline verification

The regression tests use fake HTTP transports and do not emit events to either
live PostHog project. Run them with:

```powershell
dotnet run --project Tests/Analytics.Tests/Analytics.Tests.csproj -c Release
./Tests/AnalyticsBuild.Tests.ps1
```
