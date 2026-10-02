# Main flow checks

Run `dotnet run --project Tests/MainFlow.Tests/MainFlow.Tests.csproj -c Release` from the repository root.

This console harness links the production flow state and apply-result types. It tests the GIF fast path, video preparation and preview, draft-preserving navigation, edit invalidation, transactional source replacement, operation tokens, stale completions, busy guards, apply outcome handling, and reset readiness. It does not load WinUI, invoke Windows APIs, access files, or send analytics events.

The normal Windows CI regression step discovers this project automatically alongside the other `Tests/**/*.csproj` console harnesses.
