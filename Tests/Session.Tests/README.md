# Session lifecycle checks

Run `dotnet run --project Tests/Session.Tests/Session.Tests.csproj` from the repository root.

This console harness links the production session, collection, analysis, and report classes. Windows notifications, environment collection, and applying are replaced with test doubles. It never locks the desktop, changes ACLs, or invokes the Windows image-setting API. All source, cache, and report files live beneath a unique temporary directory that is removed afterward.

Coverage includes apply/lock/unlock completion, fresh boundary hashes, immutable snapshots, current-session replacement, no automatic report storage, explicit exports, reference-file cleanup, cancellation during applying, and interruption cleanup.
