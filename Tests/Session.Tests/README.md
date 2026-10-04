# Session lifecycle checks

Run from the repository root with the .NET 10 SDK:

```powershell
dotnet run --project Tests/Session.Tests/Session.Tests.csproj -c Release
```

This console harness links production diagnostic session, collection, analysis and reporting classes. Notifications, environment collection, applying and the privileged transport use doubles. Source/cache/report files live under a unique temporary directory removed afterward; no desktop lock, ACL change or Windows image API call is made.

Coverage includes apply/lock/unlock transitions, source-kind snapshots, boundary hashes, immutable/current-session replacement, explicit export and cleanup. Trace cases cover declined elevation, disconnects/deadlines, backlog and final drains, concurrent finish, retention and shutdown boundaries. The optional Apply file-read check covers fresh target evidence, completion, timeout, relock and unavailable tracing.

These simulated lifecycles do not establish native notification/provider coverage or visible animation. See [diagnostics](../../docs/diagnostics.md) for real-machine validation and interpretation limits.
