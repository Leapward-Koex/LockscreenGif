# Hardware decoding probes

These four tiny clips contain only FFmpeg's generated `testsrc2` pattern. They
contain no user media or audio and are safe to ship with the application.

Each has exactly three 320 × 180, 8-bit YUV 4:2:0 frames at 30 fps:

| Asset | Codec | Container |
| --- | --- | --- |
| `h264.mp4` | H.264 | MP4 |
| `hevc.mp4` | HEVC | MP4 |
| `vp9.webm` | VP9 | WebM |
| `av1.ivf` | AV1 | IVF |

Generate them from the repository's bundled FFmpeg 7.1.1 build with
`./LockScreenGif/Assets/VideoDecoding/Generate-Probes.ps1`. The script records
all encoding parameters and needs no external files or downloads. The 320 × 180
dimensions avoid unusually small surfaces that some hardware decoders reject.

Discovery tests actual D3D11 output and successful process completion, trying
these codecs in the order above on each non-software DXGI adapter. Three decoded
hardware frames are required. A successful probe establishes that the machine
can use hardware decoding; individual videos can still require CPU fallback.
