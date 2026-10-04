# Frame-based video editing

## Choose, Edit, Set, Applied

Videos follow **Choose → Edit → Set → Applied**. Existing GIFs skip Edit. Cancelling a picker or failing to prepare a replacement preserves the previous draft. The Choose page links to removal controls in Settings.

Edit contains the preview, timeline and exact trim controls. Resolution and frame rate are in the collapsed **Output settings** section with a visible summary. **Continue** commits time fields and generates the GIF, with progress in the fixed footer. An unchanged generated output is reused. Committed trim/output changes invalidate it; scrubbing and playback do not. Pending or invalid time text blocks the old output, and Escape restores the field without invalidating an unchanged edit.

Set previews the actual GIF. **Save GIF…** is available for generated output; **Set lock screen** explicitly applies it. Successful Apply opens Applied. Failure, partial changes and cancellation remain on Set with the GIF available for retry and Diagnostics. Updating cache files is not proof of visible animation. The optional **Lock now** file-read check requires an explicit choice and does not measure animation.

The current breadcrumb is a non-clickable label announced as the current step. Other breadcrumbs and Back preserve the draft where navigation is valid; busy operations disable navigation. After success, Choose, Edit and Set return to their corresponding stages. Settings/Diagnostics round trips preserve the current stage; Diagnostics' Choose source action opens Choose without discarding the draft. Draft state is not restored at the next launch. Save a generated GIF to keep a reusable copy.

## Editing and playback

Trim uses source frames independently of the GIF frame-rate setting. The interval is `[startFrame, endFrame)`: End is the boundary after the last included frame, and moving End previews that last included frame. At least one frame must remain selected.

- Click the filmstrip or drag the playhead to preview a frame. Arrow keys on the focused playhead step one source frame.
- Drag trim handles, use their arrow/Home/End keys, or use Start/End −1/+1 frame buttons. Separate handle hit areas keep short selections adjustable.
- Start/End cards show time, included frame number and **Set start/end to current frame**. Enter seconds, `mm:ss.fff` or `h:mm:ss.fff`; values snap to the nearest frame boundary. Enter commits; Escape restores. Invalid text stays visible and blocks generation.
- **Reset trim** selects the whole video. Start and End jump to the first/last included preview frame without changing trim. They are disabled while playing.
- When Windows playback is available, Play loops the selection until paused. Paused trimming remains paused; interactions during playback resume within the selection. The playhead follows the media clock on each UI render, so buffering does not advance it by wall time. Pausing/editing snap to exact frames, and rendering callbacks detach on pause, navigation and close.

Windows playback is optional. If Windows metadata cannot be read for a supported fallback error, the bundled FFmpeg decoder reads first-frame dimensions and timing before indexing. If the Windows preview cannot open or later fails, the app shows **Video playback is unavailable**, disables Play, and retains frame previews, trimming and GIF generation. Those operations can continue without a Windows codec extension when the bundled decoder supports the source. Corrupt or undecodable input still fails preparation and preserves the previous draft.

## Decoder and encoder pipeline

`VideoFrameService.IndexAsync` decodes the first video stream and records presentation-order timestamps from FFmpeg `showinfo`. Integer PTS and stream time base preserve fractional and variable frame rates; frame count is not inferred from nominal FPS. Invalid/non-increasing timestamps are rejected. The final decoded frame duration falls back to nominal FPS only when needed. Fallback metadata reads one decoded frame rather than adding another full indexing pass and follows FFmpeg's automatic rotation.

The CPU index pass skips decoder loop filtering and automatic image rotation because it retains only timestamps. It still decodes every source frame, including reordered B-frames. Metadata, still previews, thumbnails and export retain normal pixel decoding and orientation. This avoids spending time improving or rotating pixels that the index immediately discards; it does not use an approximate FPS-based index or skip source frames.

### Hardware decoding

Settings includes **Video editing → Use hardware decoding**. The preference defaults to enabled and is stored independently in `%LOCALAPPDATA%/LockscreenGif/video-editing.json` using a temporary file and atomic replacement. A save failure keeps the chosen value for the current session and shows the existing Settings warning/retry pattern. Changes apply on the next video selection.

The toggle remains visible while checking, supported, unsupported or unable to verify availability. Only a verified supported machine enables interaction. Other states show an off, disabled toggle and explain the state; they never overwrite the saved preference. Unsupported machines say “Hardware decoding isn’t available on this machine. Videos will use CPU decoding.” A failed check offers another attempt on the next Settings visit.

After activation, the shared capability service checks non-software DXGI adapters in the background. It decodes the bundled synthetic H.264, HEVC, VP9 and AV1 samples in turn until one adapter succeeds. Success requires three actual `d3d11` output frames and a successful FFmpeg exit. Each probe is bounded to five seconds and discovery to fifteen seconds. Timeouts, missing samples and launch failures produce an unverified state, not an unsupported-machine claim. Success is cached for the session; unsuccessful discovery is retried on the next Settings visit. App shutdown cancels discovery.

Each video load snapshots the preference and verified adapter before metadata work begins. If discovery is pending or unavailable, that load uses CPU immediately. Hardware indexing uses D3D11VA on the selected adapter, disables autorotation and retains GPU surfaces through the timestamp-only `showinfo=checksum=0` pass. It validates each frame's hardware format, presentation order and integer timestamps, FFmpeg's final output count/completion marker and final-frame duration. It does not download pixels to system memory.

A hardware initialization, decoding, validation or ten-second no-frame-progress failure stops and drains that process, discards its partial index and retries the existing optimized CPU pass once. Cancellation never starts fallback. CPU failure follows normal video-load error handling and keeps the previous selection. Fallback is recorded in the local log without a dialog and does not change machine availability or the saved preference. The original CPU-only `IndexAsync` overload remains available for deterministic callers; the options overload returns the completed index, observed decoder, fallback flag and total indexing time including a failed hardware attempt and retry.

Only indexing uses this setting. Metadata, paused previews, timeline thumbnails and GIF generation keep their existing paths. GPU initialization and pixel readback outweighed the decoding benefit in thumbnail experiments. A local integrated benchmark on the supplied 9,776-frame video measured CPU indexing at **19.17 s** and D3D11 indexing at **12.20 s** (about **36% faster**), with identical timestamps and final duration. Synthetic 24-frame clips took about 42–54 ms on CPU and 283–296 ms on GPU; unsupported FFV1 took 285 ms including fallback. These are single-machine measurements, not guaranteed improvements for every clip.

Export uses `trim=start_frame=…:end_frame=…`, timestamp normalization, optional frame reduction, scaling and timestamp-preserving PNG output. There is no intermediate video transcode. Reduced-rate output retains the first and last selected frames even when their spacing is shorter than the requested sampling interval.

Gifski receives the selected timestamps, offset by the final frame duration for its positive-first-PTS final-delay convention. GIF delays remain limited to hundredths of a second. The encoder uses the exported PNG dimensions explicitly, preserving the selected resolution rather than allowing Gifski's automatic downscaling. Each encoder is finalized once; a successful DLL load is retained for the process lifetime so native workers cannot outlive the module.

Paused previews seek to a small neighborhood using the captured frame index, then verify every returned frame timestamp and image count against that index. Inputs that cannot be sought and verified use sequential ordinal extraction, preserving the same source frames as export. The neighborhood caches up to 32 PNGs in memory; timeline thumbnails load separately. New preview requests debounce/cancel older requests. Windows playback can show a seek result before the exact still is ready; the preview loading indicator ends when the verified still is displayed, fails, or is cancelled. A black timeline spinner ends on thumbnail success, failure or cancellation, and a stale request cannot replace a newer video's thumbnails.

The timeline samples up to eight distinct indexed frames. Longer videos use input timestamp seeking with at most two simultaneous decoders, avoiding another decode through almost the entire source. The index retains the first decoded timestamp so video streams delayed after audio seek correctly. Each seek starts just before the target timestamp and verifies the returned frame timing. Small clips (last sampled ordinal below 512), negative source starts, and inputs that cannot seek reliably use sequential ordinal extraction. Preview and export image quality and rotation remain unchanged.

Initial preparation shows an overlay outside the scroll area with filename, stage, discovered frame count, a progress bar and Cancel/Escape. During frame loading, the bar shows an estimated percentage based on decoded presentation time and the duration already reported by the decoder; it does not add another scan or estimate frame counts from nominal FPS. Variable frame rates use actual timestamps. Percentages stay below 100 until indexing succeeds. Unknown duration or ambiguous source offsets keep the bar indeterminate, as do metadata reading, opening the preview and cancellation. A CPU retry resets the bar to reflect the restarted work. Editing stays disabled until indexing and the preview-opening attempt finish. Cancelling preparation keeps the previous selection. Uncached stills show a smaller preview spinner while the editor remains usable. Conversion has progress but does not expose the preparation Cancel button.

Metadata, decoding, parsing and PNG work run in background tasks. Frame-count progress is throttled to five updates per second plus initial/final updates. The initial index uses up to eight decoder threads; other decodes and encoders use up to four. Decoder thread counts remain capped at half the logical processors (at least one thread), with two filter threads and below-normal priority. Cancellation stops the owned decoder. The space warning estimates extracted PNG usage; the GIF needs additional space while those frames remain, and actual use varies with the source.

## Saving generated output

Save copies the prepared GIF bytes into the picker-owned destination, truncates any previous tail, flushes and closes both streams. It does not re-encode or change resolution. Local providers with ID `computer` or `local` bypass cached-file provider updates. Other or unknown providers must return `Complete` or `CompleteAndRenamed` before the app reports **GIF saved**. Real write/provider failures remain failures; cancelling the picker keeps the generated output available. Save is not an atomic overwrite guarantee.

Reference contracts: [FFmpeg trim/showinfo](https://ffmpeg.org/ffmpeg-filters.html#trim), [FFmpeg decoder options](https://ffmpeg.org/ffmpeg-codecs.html#Codec-Options), [Gifski timestamps](https://github.com/ImageOptim/gifski/blob/main/gifski.h).

## Validation

Run from the repository root with the .NET 10 SDK. The native video suite requires Windows x64 and the bundled decoder/encoder plus their native dependencies.

```powershell
dotnet run --project Tests/MainFlow.Tests/MainFlow.Tests.csproj -c Release
dotnet run --project Tests/VideoEditing.Tests/VideoEditing.Tests.csproj -c Release
dotnet run --project Tests/HardwareDecoding.Tests/HardwareDecoding.Tests.csproj -c Release
dotnet run --project Tests/VideoIndexing.Tests/VideoIndexing.Tests.csproj -c Release
dotnet run --project Tests/WindowLifecycle.Tests/WindowLifecycle.Tests.csproj -c Release
dotnet run --project Tests/PickedFileWriter.Tests/PickedFileWriter.Tests.csproj -c Release
dotnet build LockScreenGif/LockscreenGif.csproj -p:Platform=x64
```

The [video suite](../Tests/VideoEditing.Tests/README.md) generates isolated fixtures for frame/timestamp, fallback metadata, cancellation and encoder checks. [Flow](../Tests/MainFlow.Tests/README.md), [lifecycle](../Tests/WindowLifecycle.Tests/README.md) and [save](../Tests/PickedFileWriter.Tests/README.md) harnesses use doubles and do not modify Windows settings. Their success does not establish native picker/player behavior or secure-screen animation.

The hardware capability and indexing harnesses use fake adapters/processes for ordinary regression runs. [Optional native hardware integration checks](../Tests/VideoIndexing.Tests/README.md) compare every timestamp, source offset and final duration against CPU for supported codecs, B-frames, variable rates and rotated video; they also benchmark initialization and fallback costs. Analytics tests use fake transports for decoder/preference/fallback/duration fields and opt-out behavior.

Manual Windows checks remain:

- On supported and unsupported machines, confirm the Video editing card is visible and responsive; checking/failed/unavailable explanations match the disabled state. Toggle off, restart and verify it stays off. Change hardware availability and confirm the saved value is restored when support returns. Cancel a replacement during GPU loading and confirm the prior selection remains usable.

1. Walk GIF and video routes at normal/minimum window size and increased scaling. Check breadcrumbs, fixed footer, focus, Back/Continue reuse, pending invalid text/Escape and Settings/Diagnostics round trips.
2. Check paused/playing boundary changes, one-frame clips, frame stepping, keyboard handle limits and loop alignment while resizing. Compare generated first/last frames to exact still previews.
3. Cancel a replacement during preparation. Check responsive loading/thumbnails and that only the latest preview controls its spinner. On a machine without a compatible Windows codec, confirm the playback warning and disabled Play while frame editing and generation still work.
4. Generate repeatedly, save Original and smaller resolutions, overwrite a longer local GIF, and check a provider destination where available. Confirm dimensions, usable GIF bytes and accurate success/failure feedback.
5. Close with one X click while previewing, from Settings/Diagnostics and during pending apply/verification/feature cleanup. Native close/message-loop behavior requires this check even though managed lifetime orderings are covered.
6. Apply, retry failures and observe both actual lock and sign-in screens. Cache hashes and file-read traces alone cannot prove animation.
