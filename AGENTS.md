# Repository guidance

## C# formatting

Use the pinned CSharpier tool and the repository `.editorconfig` for C# formatting.
Run `dotnet tool restore` when needed, then `./scripts/Format-CSharp.ps1` after C#
edits and `./scripts/Format-CSharp.ps1 -Check` before finishing. The script applies
IDE0011 required braces before CSharpier's 140-column layout, including test
projects outside the solution. Missing braces are also build errors. Do not
format generated files or vendor sources, or run a competing
whitespace formatter over CSharpier's output. See [docs/code-style.md](docs/code-style.md).

## Feature development and analytics

For each new or changed feature, consider whether analytics would answer a
concrete usage, reliability, or performance question. Add events only when they
provide useful information; do not track every click, progress tick, or frame.
See [docs/analytics.md](docs/analytics.md) for the event contract and measurement
recipes, and update that document when adding or changing events.

Use the existing typed `AnalyticsService` with stable event names and allowlisted
properties. Track meaningful accepted actions and actual outcomes, separating
success, partial failure, cancellation, and unobserved results. Correlate start
and completion events with a random operation ID when measuring a funnel.
Keep normal usage separate from diagnostic workflows, and never treat a verified
cache write as proof that Windows displayed an animation.

Analytics is optional: preserve the saved opt-out and development/production
routing. Capture calls must not await network or hold application locks while
sending. Offline operation, blocked connections, slow responses, rejected events,
and analytics failures must not delay or fail app operations or shutdown. Keep
queues and request lifetimes bounded; dropping telemetry is preferable to
impairing the app. Add fake-transport regression coverage for relevant failure
and delay cases; automated tests must not send real events.

Never send filenames, paths, media, diagnostic reports, free-form text, exception
messages, or user/Windows identifiers. Use existing random installation/session
IDs and typed, bounded metadata. Do not introduce global input monitoring,
session replay, or background heartbeats to inflate usage measurements.

## Diagnostic log analysis

Use [.agents/skills/analyze-lockscreen-logs/SKILL.md](.agents/skills/analyze-lockscreen-logs/SKILL.md) when investigating LockscreenGif logs, diagnostic reports, misleading findings, or collector behavior.

Continuously update and iterate on this skill as investigations reveal new issues, useful checks, or improvements to log analysis. Update its guidance, references, or analysis script in the same change that establishes the lesson. Keep observations distinct from hypotheses and add focused regression coverage when script behavior changes.

Keep the skill and its supporting files committed in this repository. Do not commit user reports, logs, personal paths, or exported diagnostic ZIPs; use small synthetic fixtures for tests.
