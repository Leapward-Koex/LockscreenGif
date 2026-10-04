# Diagnostic evidence checks

Run from the repository root with the .NET 10 SDK:

```powershell
dotnet run --project Tests/Diagnostics.Tests/Diagnostics.Tests.csproj -c Release
```

This console harness links production inspection, collection, analysis and report code to synthetic evidence. It covers malformed/truncated GIFs, changing or incomplete caches, evidence timing/attribution, finding grouping, feature-state/action history, trace shutdown/retention, privacy redaction, log attachments and report/ZIP serialization. Test files live in an isolated temporary directory removed afterward.

No Windows lock-screen setting is changed, feature setter is invoked or elevation requested. Synthetic cache writes, reads and trace events do not demonstrate secure-screen animation. Native collector/provider coverage, lock/unlock notifications, elevation cancellation, display/power changes and actual lock/sign-in playback require separate validation. See [diagnostics](../../docs/diagnostics.md) for evidence limits and the current manual procedure.
