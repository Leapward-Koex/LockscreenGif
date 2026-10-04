# Lockscreen Gif

Set an animated GIF as your Windows lock screen, or convert a video into a GIF
first. The app applies GIFs through the Windows lock-screen image cache.

# Video file input demo

https://github.com/user-attachments/assets/1dc4ec39-2f38-42c6-8216-3c911f6d8bc9

# Gif file input demo


https://github.com/Leapward-Koex/LockscreenGif/assets/30615050/7448e59f-9767-4509-8ce3-721cf1783faa


# Use the app

1. Select **Picture** in Windows **Settings > Personalization > Lock screen**.
   If the app reports a missing image cache, choose a picture, then lock and
   unlock Windows once.
2. Follow the app's **Prerequisites** panel. If it offers **Disable Windows
   feature**, allow the requested elevation. A reboot may be needed for Windows
   to use the change; the app offers **Not now** and **Reboot**. See the
   [Windows feature guide](docs/windows-image-feature.md).
3. Select **Choose GIF**, or **Choose video** and edit the clip. For video,
   select **Continue** to generate the GIF preview. Then select **Set lock screen**.
   **Save GIF…** keeps a reusable copy of a generated GIF.
4. Lock Windows to check animation on the clock and sign-in screens. A successful
   apply confirms that the cache files were updated; it does not verify playback.

The applied image must be a real GIF. Video input is converted using
[FFmpeg](https://www.ffmpeg.org/) and [Gifski](https://gif.ski/).

# Logs

Open **Settings > Logs** to open the log folder or choose **Save logs as ZIP…**
and select a destination. The ZIP contains the app's `app_*.log` files, including
the current log. Export runs in the background and leaves the original logs in
place. These are copies of the original logs, not the redacted Diagnostics report;
crash dumps, analytics settings, and other files in the folder are not included.

**Diagnostics > Export report** also includes recent application logs, redacted
alongside the diagnostic evidence. Its `logs/manifest.json` lists unavailable or
shortened attachments. See [diagnostics documentation](docs/diagnostics.md).

# Analytics

Usage analytics are enabled on first launch, with a notice and an opt-out under
**Settings > Analytics**. Events go to PostHog in the EU; filenames, media, and
diagnostic logs are excluded. See [analytics documentation](docs/analytics.md)
for events, delivery limits, configuration, and testing.

# C# formatting

Run `dotnet tool restore`, then `./scripts/Format-CSharp.ps1` from the repository
root on Windows to add required braces and format all C# source. Use
`./scripts/Format-CSharp.ps1 -Check` to verify both rules without changing files.
See [the code style guide](docs/code-style.md) for the shared rules and Visual
Studio format-on-save setup.

# Build and package

Build on Windows using the SDK selected by [global.json](global.json) and the
MSBuild setup in the [Windows build workflow](.github/workflows/dotnet-desktop.yml).
From the repository root, publish and check the actual distribution folder:

```powershell
dotnet publish LockScreenGif/LockscreenGif.csproj -c Release -p:PublishProfile=FolderProfile.pubxml
pwsh -NoProfile -File scripts/Test-PrivilegedPackaging.ps1
pwsh -NoProfile -File Tests/ReleasePackaging.Tests.ps1
```

The folder profile publishes to `artifacts/app`. The checks require PowerShell 7
and validate the helper, native runtime/provenance and MSI payload inventory.
Build `DefaultBuild` from `Installer/LockscreenGif.aip` with Advanced Installer
23.3, setting its numeric product version to match the app's numeric version.
The workflow contains the versioned MSI and ZIP packaging steps.

# Automated prereleases

Builds use .NET 10 LTS. The Windows .NET reference package is
10.0.26100.87; the existing minimum Windows build remains 26100. Newer Windows
builds are not excluded by the installer.

Successful master builds publish a `build-<workflow run number>` GitHub prerelease
with an x64 MSI, app ZIP, checksums, and commit changelog. Pull requests build and
test with read-only permissions; only the master release job can publish.

The installer and app share version `3.<run / 65535 rounded down>.<run modulo 65535>`.
For example, run 42 is `3.0.42`. Epoch 3 sorts after older `2.1.N` installers;
the rollover respects MSI and .NET version limits. Do not reset the workflow
counter without advancing the epoch. Counters above 16,776,959 fail explicitly.

Reruns keep their version and tag, while the embedded informational version
records the commit and attempt (for example `3.0.42-ci+<commit>.attempt.2`).
Stale reruns cannot replace releases after master advances. Local builds display
`3.0.0-local` (plus the source revision supplied by the .NET SDK).
The informational version appears in startup logs and the Diagnostics footer.

Run `./Tests/BuildVersion.Tests.ps1` to check version boundaries and reruns.
