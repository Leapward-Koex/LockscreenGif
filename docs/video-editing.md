# Frame-based video editing

The editor selects source frames, independently of the GIF frame-rate setting. The first and last included frames are shown beside the Start and End fields. End is the boundary immediately after the last included frame; moving End previews that last included frame.

- A single Play/Pause button sits directly below the timeline. Click the filmstrip or drag the playhead to preview a source frame. Arrow keys on the focused playhead step exactly one source frame.
- Drag either trim handle, use its arrow keys, or use the Start/End −1 frame and +1 frame buttons. Home/End move a focused handle to its allowed limits. Both handles span the full filmstrip height, with separate hit areas outside the selected range so very short selections remain adjustable.
- Each Start/End card groups its time, included-frame number, step buttons, and Set start/end to current frame action. The set actions include the current preview frame at that boundary. These cards stack vertically when the editor is narrow.
- Enter seconds, `mm:ss.fff`, or `h:mm:ss.fff`. Values snap to the nearest source-frame boundary. Enter commits; Escape restores that field. Invalid input stays visible with an error and blocks generation until corrected.
- Reset trim, beside the clip summary, selects the full video. Duration presets are omitted.
- Play previews the selected clip and loops automatically until paused. Paused trimming stays paused. A drag during playback resumes playback; scrubbing within the selection resumes at the playhead.

During playback, the playhead follows the media clock on each UI render, including fractional positions between source frames. Its marker and line move using render transforms. Pausing and editing still snap to exact source frames. The render callback is detached on pause, navigation away, and window close. Playback uses the player's position rather than elapsed wall time, so buffering does not make the marker run ahead.

## Pipeline

`VideoFrameService.IndexAsync` decodes the first video stream once and records presentation-order frame timestamps from the bundled FFmpeg's `showinfo` output. It uses integer PTS and the stream time base rather than the rounded diagnostic timestamp string. Frame count is not inferred from nominal FPS, so fractional and variable frame rates are supported. Invalid/non-increasing timestamps are rejected. The final frame uses its decoded duration, with nominal FPS as a fallback if that duration is missing.

The internal trim interval is `[startFrame, endFrame)`, and must contain at least one frame. Export uses FFmpeg's `trim=start_frame=…:end_frame=…` followed by timestamp normalization, optional frame reduction, scaling, and PNG output with timestamp passthrough. There is no intermediate video transcode. Reduced frame-rate options retain the first and last selected source frames, even when their spacing is shorter than the requested sampling interval.

Gifski receives the selected presentation timestamps, preserving variable frame delays. All timestamps are offset by the final frame's duration, using Gifski's documented positive-first-PTS convention for the final delay. GIF timing remains limited to the format's hundredths-of-a-second precision; the selected source-frame boundaries are exact.

Paused previews use the same ordinal frame selection as export, rather than trusting a media-player seek. The editor decodes a small neighborhood and caches up to 32 PNGs for subsequent steps. A live seek is shown while an exact still loads. Preview requests are debounced and cancelled when superseded. Timeline thumbnails are decoded separately. These previews stay in memory and do not share generation's temporary-directory cleanup.

Indexing and uncached previews decode source video, so long/high-resolution videos can take longer to open or seek. Initial loading displays a page-level overlay outside the scroll area with the filename, the current stage, the discovered frame count, and Cancel (also Escape). Editing is disabled until scanning and opening the media player both finish. Cancellation keeps the previous selection. Uncached frame previews show a smaller spinner over the video while the rest of the editor remains usable; a newer request supersedes the old one.

Metadata/media opening, FFmpeg startup and parsing, and timestamp/PNG processing run on background tasks. Frame-count updates are throttled to five per second, plus initial/final updates. FFmpeg uses at most four decoder/encoder threads, two filter threads, and below-normal process priority to leave CPU headroom for the UI. Cancellation stops the decoder without enumerating a process tree. The regression suite invokes indexing, preview, thumbnails, and export from a simulated UI synchronization context and checks that their processing never posts a continuation back to it.

Reference behavior: [FFmpeg trim and showinfo filters](https://ffmpeg.org/ffmpeg-filters.html#trim), [Gifski presentation timestamp contract](https://github.com/ImageOptim/gifski/blob/main/gifski.h).

## Validation

Run from the repository root:

```powershell
dotnet run --project Tests/VideoEditing.Tests/VideoEditing.Tests.csproj
dotnet build LockScreenGif/LockscreenGif.csproj -p:Platform=x64
```

The integration checks generate local fixtures and use the bundled FFmpeg/Gifski binaries. They verify frame counts, byte-for-byte source/export matches, exclusive end boundaries, one-frame clips, fractional and variable frame rates, exact still previews, thumbnails, reduced-rate boundaries, GIF timing, time parsing, and cancellation. Tests use a unique temporary directory and do not touch lock-screen settings.

Manual UI checks still required when Windows UI automation is available:

1. Load `Demos/Video Example.mp4`; confirm the timeline, labels, and thumbnails fit at normal and narrow window widths and increased display scaling.
2. Pause, drag each boundary, and release: remain paused on its included boundary frame. Repeat while playing: resume playback. Drag the playhead during playback and verify it resumes at that location within the selection.
3. Step backward and forward, including the first/last source frames and a one-frame selection. Verify button limits, distinct handle hit areas, keyboard focus, and screen-reader range values.
4. Enter invalid, crossed, out-of-range, and hour-long times. Verify Enter/Escape, preserved invalid text while scrubbing, and disabled generation until correction.
5. Try Reset trim, Set start/end to current frame, and the Play/Pause button. Confirm playback loops automatically for both trimmed clips and the full video. Confirm the playhead moves continuously, stays aligned with the video, stops when paused, and jumps directly to the start on looping. Resize during playback, then drag or use arrow keys to confirm the visual marker and hit target remain aligned.
6. Scroll away from the editor and load a replacement video: confirm the loading overlay stays visible, the window remains responsive, and Cancel/Escape restores the previous selection. Confirm repeated frame steps keep the editor responsive and only the latest preview request controls its loading indicator. Generate and save a short GIF and compare its first/last frames to the exact still previews.
