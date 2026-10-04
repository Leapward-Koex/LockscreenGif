# Main flow checks

Run from the repository root with the .NET 10 SDK:

```powershell
dotnet run --project Tests/MainFlow.Tests/MainFlow.Tests.csproj -c Release
```

This console harness links the production flow state and apply-result types. It covers the GIF fast path, video generation readiness, draft-preserving breadcrumbs/Back, pending edits and output invalidation, transactional replacement, busy guards and stale operation tokens. Apply outcomes distinguish verified success, failure, partial changes and cancellation; recovery messages and prepared GIFs remain available for retry.

It does not load WinUI, decode media, invoke Windows APIs, access user files or send analytics events. Windows CI discovers it with the other console harnesses. See [video editing](../../docs/video-editing.md) for the real UI and playback checks that state tests cannot establish.
