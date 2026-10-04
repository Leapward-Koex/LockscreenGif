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

The index pass skips decoder loop filtering and automatic image rotation because it retains only timestamps. It still decodes every source frame, including reordered B-frames. Metadata, still previews, thumbnails and export retain normal pixel decoding and orientation. This avoids spending time improving or rotating pixels that the index immediately discards; it does not use an approximate FPS-based index or skip source frames.

Export uses `trim=start_frame=…:end_frame=…`, timestamp normalization, optional frame reduction, scaling and timestamp-preserving PNG output. There is no intermediate video transcode. Reduced-rate output retains the first and last selected frames even when their spacing is shorter than the requested sampling interval.

Gifski receives the selected timestamps, offset by the final frame duration for its positive-first-PTS final-delay convention. GIF delays remain limited to hundredths of a second. The encoder uses the exported PNG dimensions explicitly, preserving the selected resolution rather than allowing Gifski's automatic downscaling. Each encoder is finalized once; a successful DLL load is retained for the process lifetime so native workers cannot outlive the module.

Paused previews use the same ordinal frame selection as export. A small decoded neighborhood caches up to 32 PNGs in memory; timeline thumbnails load separately. New preview requests debounce/cancel older requests. A black timeline spinner ends on thumbnail success, failure or cancellation, and a stale request cannot replace a newer video's thumbnails.

The timeline samples up to eight distinct indexed frames. Longer videos use input timestamp seeking with at most two simultaneous decoders, avoiding another decode through almost the entire source. The index retains the first decoded timestamp so video streams delayed after audio seek correctly. Each seek starts just before the target timestamp and verifies the returned frame timing. Small clips (last sampled ordinal below 512), negative source starts, and inputs that cannot seek reliably use sequential ordinal extraction. Preview and export image quality and rotation remain unchanged.

Initial preparation shows an overlay outside the scroll area with filename, stage, discovered frame count and Cancel/Escape. Editing stays disabled until indexing and the preview-opening attempt finish. Cancelling preparation keeps the previous selection. Uncached stills show a smaller preview spinner while the editor remains usable. Conversion has progress but does not expose the preparation Cancel button.

Metadata, decoding, parsing and PNG work run in background tasks. Frame-count progress is throttled to five updates per second plus initial/final updates. The initial index uses up to eight decoder threads; other decodes and encoders use up to four. Decoder thread counts remain capped at half the logical processors (at least one thread), with two filter threads and below-normal priority. Cancellation stops the owned decoder. The space warning estimates extracted PNG usage; the GIF needs additional space while those frames remain, and actual use varies with the source.

## Saving generated output

Save copies the prepared GIF bytes into the picker-owned destination, truncates any previous tail, flushes and closes both streams. It does not re-encode or change resolution. Local providers with ID `computer` or `local` bypass cached-file provider updates. Other or unknown providers must return `Complete` or `CompleteAndRenamed` before the app reports **GIF saved**. Real write/provider failures remain failures; cancelling the picker keeps the generated output available. Save is not an atomic overwrite guarantee.

Reference contracts: [FFmpeg trim/showinfo](https://ffmpeg.org/ffmpeg-filters.html#trim), [FFmpeg decoder options](https://ffmpeg.org/ffmpeg-codecs.html#Codec-Options), [Gifski timestamps](https://github.com/ImageOptim/gifski/blob/main/gifski.h).

## Validation

Run from the repository root with the .NET 10 SDK. The native video suite requires Windows x64 and the bundled decoder/encoder plus their native dependencies.

```powershell
dotnet run --project Tests/MainFlow.Tests/MainFlow.Tests.csproj -c Release
dotnet run --project Tests/VideoEditing.Tests/VideoEditing.Tests.csproj -c Release
dotnet run --project Tests/WindowLifecycle.Tests/WindowLifecycle.Tests.csproj -c Release
dotnet run --project Tests/PickedFileWriter.Tests/PickedFileWriter.Tests.csproj -c Release
dotnet build LockScreenGif/LockscreenGif.csproj -p:Platform=x64
```

The [video suite](../Tests/VideoEditing.Tests/README.md) generates isolated fixtures for frame/timestamp, fallback metadata, cancellation and encoder checks. [Flow](../Tests/MainFlow.Tests/README.md), [lifecycle](../Tests/WindowLifecycle.Tests/README.md) and [save](../Tests/PickedFileWriter.Tests/README.md) harnesses use doubles and do not modify Windows settings. Their success does not establish native picker/player behavior or secure-screen animation.

Manual Windows checks remain:

1. Walk GIF and video routes at normal/minimum window size and increased scaling. Check breadcrumbs, fixed footer, focus, Back/Continue reuse, pending invalid text/Escape and Settings/Diagnostics round trips.
2. Check paused/playing boundary changes, one-frame clips, frame stepping, keyboard handle limits and loop alignment while resizing. Compare generated first/last frames to exact still previews.
3. Cancel a replacement during preparation. Check responsive loading/thumbnails and that only the latest preview controls its spinner. On a machine without a compatible Windows codec, confirm the playback warning and disabled Play while frame editing and generation still work.
4. Generate repeatedly, save Original and smaller resolutions, overwrite a longer local GIF, and check a provider destination where available. Confirm dimensions, usable GIF bytes and accurate success/failure feedback.
5. Close with one X click while previewing, from Settings/Diagnostics and during pending apply/verification/feature cleanup. Native close/message-loop behavior requires this check even though managed lifetime orderings are covered.
6. Apply, retry failures and observe both actual lock and sign-in screens. Cache hashes and file-read traces alone cannot prove animation.
