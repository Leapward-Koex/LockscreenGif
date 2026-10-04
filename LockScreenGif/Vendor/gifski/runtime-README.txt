Microsoft Visual C++ runtime dependency for Gifski

vcruntime140.dll is an unmodified Microsoft Visual C++ x64 release runtime
redistributable. Gifski imports this library. It is deployed beside gifski.dll
so video-to-GIF generation does not require a separately installed VC runtime.

File version: 14.51.36247.0
SHA-256: D1F4225DF2CD877DBF130D5668A021DCE3F94118455FF5EC952061C30AFC9CE7
Source: Visual Studio 2026 Community, VC/Redist/MSVC/14.51.36231/x64/
        Microsoft.VC145.CRT/vcruntime140.dll

Copyright Microsoft Corporation. All rights reserved.
This Microsoft component retains its applicable Microsoft license terms.
The LockscreenGif MIT license does not license this component.
Redistribution is subject to the applicable Visual Studio license terms and
Microsoft's REDIST list; debug and preview files are not redistributable.

REDIST list:
https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution
License directory:
https://visualstudio.microsoft.com/license-terms/
Deployment guidance:
https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files

App-local runtime copies must be serviced through future application releases.
When updating this file, use an official matching x64 release redistributable,
refresh runtime-provenance.json, verify dependencies, and test both published
installation formats on Windows without a system VC runtime.
