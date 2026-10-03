# Video editing checks

Run `dotnet run --project Tests/VideoEditing.Tests/VideoEditing.Tests.csproj` from the repository root on Windows x64. This console suite links the production frame model, decoder/export service, and GIF encoder. It uses the bundled FFmpeg and Gifski binaries and only writes to its own temporary directory.

See [video-editing.md](../../docs/video-editing.md) for the editing semantics, pipeline design, and remaining manual UI checks.

The suite prints a `RUN` marker before native GIF encoding and decoder round-trip. If output stops there without a managed exception, inspect the test process exit code; CI reports its signed decimal and unsigned 32-bit hexadecimal forms. The preceding `PASS` names the last completed check, not the failing operation.

Native lifetime checks assert that the Gifski module remains loaded after successful and failed encoding, while each encoder is finalized exactly once. `Gifski.Net` 1.2.0 disposal releases its DLL reference, but native worker teardown can outlive `Finish`; production retains one successful library load until process exit. These checks enforce that lifetime condition without provoking an unload race or claiming to reproduce an earlier CI crash. See the [native encoder investigation guidance](../../.agents/skills/analyze-lockscreen-logs/references/evidence.md#native-encoder-exits-without-a-managed-exception).
