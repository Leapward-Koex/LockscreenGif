# Video editing checks

Run from the repository root with the .NET 10 SDK on Windows x64:

```powershell
dotnet run --project Tests/VideoEditing.Tests/VideoEditing.Tests.csproj -c Release
```

This console suite links the production frame model, decoder/export service and GIF encoder. It uses bundled FFmpeg/Gifski and their native dependencies, generates fixtures in a unique temporary directory and removes that directory afterward. It covers exact frame boundaries and previews, fractional/VFR timing, reduced-rate boundaries, output dimensions, fallback metadata/rotation, invalid input, cancellation and work outside a simulated UI context.

The `RUN native GIF encoding and decoder round-trip` marker identifies entry into native encoding. If the process exits there without a managed exception, inspect its exit code; CI reports decimal and unsigned hexadecimal forms. The preceding PASS names the last completed check, not the failing operation.

Native lifetime checks enforce a process-lifetime DLL reference and exactly-once encoder finalization after success or failure. See [native encoder guidance](../../.agents/skills/analyze-lockscreen-logs/references/evidence.md#native-encoder-exits-without-a-managed-exception).

These checks do not exercise Windows codec availability, native UI/pickers or lock-screen playback, and do not prove release package completeness. See [video editing](../../docs/video-editing.md) for pipeline semantics and manual validation.
