# Window lifecycle checks

Run `dotnet run --project Tests/WindowLifecycle.Tests/WindowLifecycle.Tests.csproj -c Release`.

This harness links the production close handler and preview lifetime methods to
small WinUI/media doubles. The media double throws if paused after disposal;
the window and monitor doubles reject closing or removing message hooks inside
the original close callback. Checks cover both Closed/Unloaded orderings,
navigation preserving a video draft, repeated cleanup, an empty preview, one-click
idle shutdown, pending verification/diagnostics/apply work, repeated close clicks,
verification failure, and a dispatcher that no longer accepts work.

No Windows settings, native media APIs, files, or analytics transports are used.
The doubles do not reproduce WinUI's native message loop; also manually check
closing with a video loaded, after applying it, and from Settings/Diagnostics.
