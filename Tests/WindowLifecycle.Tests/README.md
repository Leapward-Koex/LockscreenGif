# Window lifecycle checks

Run from the repository root with the .NET 10 SDK:

```powershell
dotnet run --project Tests/WindowLifecycle.Tests/WindowLifecycle.Tests.csproj -c Release
```

This harness links production close handling and preview lifetime methods to WinUI/media doubles. It covers both Closed/Unloaded orderings, reusable drafts across navigation, repeated cleanup, frame-only drafts after playback failure and one-click shutdown. Pending apply, verification, diagnostics and explicit feature actions must drain before close; repeated clicks, cleanup failure and a stopped dispatcher are also checked.

The doubles reject pausing disposed media or closing/removing message hooks inside the original close callback. They do not reproduce WinUI's native message loop or request a real reboot. No Windows settings, native media APIs, user files or analytics transports are used.

Also manually close with a video loaded, after Apply, during pending cleanup and from Settings/Diagnostics. See [video editing](../../docs/video-editing.md#validation) for native UI checks.
