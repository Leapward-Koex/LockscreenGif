Run the dependency-free diagnostic regression checks from the repository root:

```powershell
dotnet run --project Tests/Diagnostics.Tests/Diagnostics.Tests.csproj
```

The console harness links the production GIF inspector, reference generator, evidence analyzer,
cache collector, and report writer. It exercises malformed/truncated sources, cancellation,
file changes, incomplete inventories, privacy redaction, snapshot serialization, and ZIP exports.
It creates an isolated temporary directory and removes it afterward.

These checks do not change the Windows lock screen or request elevation. Real-machine testing
must additionally cover lock/unlock notifications, the optional Windows image-setting API,
cancelled elevation, display changes, sleep/resume, and visible animated playback.
