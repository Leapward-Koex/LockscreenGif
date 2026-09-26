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
`%LOCALAPPDATA%/LockscreenGif/analytics.json`. Opt-out discards queued events,
cancels active delivery, and removes the local identifier. Re-enabling generates
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
| `page_viewed` | Lockscreen, Diagnostics, or Settings was opened. |
| `gif_selected` | GIF selection completed, failed, or was cancelled; not a decoding or playback check. |
| `video_load_started` | A selected video began indexing and preview initialization. |
| `video_load_completed` | Video indexing and preview initialization completed, failed, or were cancelled. |
| `gif_generation_started` | Conversion began with the requested clip and output settings. |
| `gif_generation_completed` | GIF generation completed, failed, or was cancelled. |
| `gif_save_completed` | Saving and file-provider completion finished, failed, or were cancelled. |
| `lockscreen_apply_started` | A lockscreen or diagnostic apply was requested. |
| `lockscreen_apply_completed` | Apply finished with success, partial failure, failure, or cancellation. |
| `lockscreen_removal_completed` | Removal from Settings succeeded, partially failed, failed, or found nothing to remove. |
| `diagnostic_test_requested` | A diagnostic test was requested; not confirmation that the test completed. |
| `diagnostic_stop_requested` | The user requested that the diagnostic test stop. |
| `diagnostic_report_export_completed` | A diagnostic report export succeeded, failed, or was cancelled; report contents are not sent. |
| `app_error` | Best-effort error category before existing fatal-error handling. |
| `log_export_completed` | Saving a ZIP from Settings succeeded, failed, or was cancelled. Only outcome, elapsed time, and a coarse error category are sent; no log contents, names, or destination paths. |

Each started operation has a random `operation_id`, repeated on its terminal
event. It identifies one attempt, not a media file or a whole editing session.
Video picker cancellations occur before loading starts and therefore can have
a terminal event without an operation ID or a matching start. A missing terminal
event does not prove a crash: delivery is best effort.

Properties use a fixed typed allowlist:

| Properties | Meaning |
| --- | --- |
| `outcome`, `page`, `error_kind` | Bounded categories for results, navigation, and errors. |
| `operation_id`, `workflow` | Attempt correlation and `lockscreen` versus `diagnostics` apply context. |
| `duration_ms` | Elapsed operation time, excluding the file picker where applicable. |
| `requested_width`, `requested_fps` | Requested generation settings; FPS `0` means all source frames. These are not measurements of validated output media. |
| `clip_duration_seconds`, `selected_frame_count` | The requested conversion range. |
| `uses_reference_gif` | Whether a diagnostic test requested the bundled reference GIF. |
| `gif_size_bytes`, `gif_width`, `gif_height` | Source GIF file size in bytes and logical-screen width/height in pixels on `lockscreen_apply_completed`, for normal and diagnostic applies. These describe one source file, not total cache storage or monitor resolution. |
| `target_count`, `copied_count`, `verified_count`, `failed_count` | Cache operation counts. Cancelled applies omit `failed_count` because some destinations may not have been attempted. |
| `api_requested`, `api_completed` | Whether the Windows lockscreen API was requested and completed. |

Common properties are app version, Windows version, platform, environment,
random installation ID, and `$session_id`. No paths, filenames, media, hashes,
usernames, Windows SIDs, exception messages, stack traces, or diagnostic reports
are sent.

Apply size metadata comes from the same held source stream and header used by
the apply operation; it adds no separate file open or image decode. It is also
included when a later apply step fails, if the source metadata was available.
Unknown sizes/dimensions are omitted, and the start event has no size fields
because it precedes opening the source. GIF dimensions describe the header's
logical screen, not a full decoding or playback validation.

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
| What makes operations slow? | Median and p95 of `duration_ms` for successful generation/apply. Break generation down by requested width/FPS and clip duration; show sample counts. |
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
