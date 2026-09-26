# Lockscreen Gif
A simple application to set your windows lockscreen to a video or a GIF image.
You cannot set a GIF as a lockscreen using the normal windows settings even when using file extension spoofing but with some Windows system image cache modifications you can trick Windows into using a GIF on your lockscreen.

# Video file input demo

https://github.com/user-attachments/assets/1dc4ec39-2f38-42c6-8216-3c911f6d8bc9

# Gif file input demo


https://github.com/Leapward-Koex/LockscreenGif/assets/30615050/7448e59f-9767-4509-8ce3-721cf1783faa


# Notes
ONLY real .GIF files are supported.
Videos cannot work and will never work directly, please use the video option to dynamically create a gif to use.
GIFs created through the video option are created with [FFMPEG](https://www.ffmpeg.org/) and [Gifski](https://gif.ski/) to produce high quality, video like gifs.

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

# Automated prereleases

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
