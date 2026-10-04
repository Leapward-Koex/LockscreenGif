$ErrorActionPreference = 'Stop'
$ffmpeg = Join-Path $PSScriptRoot '../../Vendor/FFMPEG/ffmpeg.exe'
$common = @('-hide_banner', '-nostdin', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=30', '-frames:v', '3', '-an', '-pix_fmt', 'yuv420p')
$codecs = @(
    @{ Name = 'h264.mp4'; Arguments = @('-c:v', 'libx264', '-preset', 'medium', '-crf', '32', '-threads', '2') },
    @{ Name = 'hevc.mp4'; Arguments = @('-c:v', 'libx265', '-preset', 'medium', '-crf', '36', '-x265-params', 'pools=1:frame-threads=1:log-level=error') },
    @{ Name = 'vp9.webm'; Arguments = @('-c:v', 'libvpx-vp9', '-deadline', 'realtime', '-cpu-used', '8', '-crf', '40', '-b:v', '0', '-threads', '2') },
    @{ Name = 'av1.ivf'; Arguments = @('-c:v', 'libaom-av1', '-cpu-used', '8', '-crf', '45', '-b:v', '0', '-threads', '2', '-f', 'ivf') }
)
foreach ($codec in $codecs) {
    & $ffmpeg @common @($codec.Arguments) (Join-Path $PSScriptRoot $codec.Name)
    if ($LASTEXITCODE -ne 0) { throw "Could not generate $($codec.Name)." }
}
