# Hardware decoding capability and Settings tests

Run the deterministic regression harness:

```powershell
dotnet run --project Tests/HardwareDecoding.Tests/HardwareDecoding.Tests.csproj -c Release
```

Normal execution uses fake adapters and probes; it requires no GPU and sends no
analytics. Coverage includes multi-adapter/codec discovery, single-flight and
session caching, retries, missing assets, launch failures, timeout versus
unsupported classification, cancellation, required D3D11 frames, Settings
states, and atomic preference persistence.

To additionally enumerate this machine's real DXGI adapters and decode bundled
synthetic clips with FFmpeg, append `-- --probe`. A machine without hardware
support can report `Unavailable`; a failed or timed-out check fails this
optional integration check. This does not modify saved preferences.
