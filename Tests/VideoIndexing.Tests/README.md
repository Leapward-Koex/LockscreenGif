# Hardware indexing checks

`dotnet run --project Tests/VideoIndexing.Tests -c Release` runs deterministic fake-process checks without decoding media or requiring a GPU. It verifies saved-off/unavailable routing, actual D3D11 frames, adapter arguments, completion validation, partial-index rejection, CPU failure, process cleanup before retry, watchdog timing and cancellation.

Progress checks cover variable frame timing, delayed video streams, unknown or ambiguous duration, completion only after validation, cancellation, and resetting the estimate before a CPU retry. Native integration also checks progress from both actual decoding paths.

Opt in on a Windows machine with D3D11 video decoding:

```powershell
dotnet run --project Tests/VideoIndexing.Tests -c Release -- --hardware --adapter 0
# Optionally add --video "C:\path\to\local-video.mkv" to benchmark another video.
```

This generates small synthetic H.264/HEVC B-frame, VP9, AV1, variable-rate, rotated, source-offset and unsupported FFV1 clips. Every frame timestamp, source start and final duration must equal CPU indexing exactly, including when CPU fallback is needed. At least one test must observe actual D3D11 frames; codecs unavailable on that adapter may fall back. The BENCH lines include process initialization, validation and any failed hardware attempt plus CPU retry. Tiny clips often cost more to initialize on the GPU than they save; use a representative longer video as well. The temporary synthetic files are removed by the harness.

Capability discovery and preferences have separate deterministic tests in `Tests/HardwareDecoding.Tests`.
