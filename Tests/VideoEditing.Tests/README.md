# Video editing checks

Run `dotnet run --project Tests/VideoEditing.Tests/VideoEditing.Tests.csproj` from the repository root on Windows x64. This console suite links the production frame model, decoder/export service, and GIF encoder. It uses the bundled FFmpeg and Gifski binaries and only writes to its own temporary directory.

See [video-editing.md](../../docs/video-editing.md) for the editing semantics, pipeline design, and remaining manual UI checks.
