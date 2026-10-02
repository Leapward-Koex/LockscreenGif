# Frame-based video editing

## Main-page flow

Videos follow **Choose → Edit → Set → Applied**. Existing GIFs skip Edit.
Choose video or Choose GIF opens the existing file picker; cancelling or failing
to prepare a replacement keeps the previous draft. The Choose page also links to
the existing removal controls in Settings.

Edit contains the video preview and precise trim controls. Resolution and frame
rate are in the collapsed **Output settings** section, with a visible summary.
**Continue** commits the time fields and generates a GIF, showing progress in the
fixed footer. It never applies the animation automatically. Set shows the actual
GIF, offers **Save GIF…** for generated media, and applies only when **Set lock
screen** is selected. Setup warnings are advisory.

The current breadcrumb is an accent-filled, non-clickable label, announced as
the current step. Other breadcrumbs are enabled only when navigation is valid;
busy operations disable navigation. Back and available breadcrumbs preserve the draft. An unchanged video preview
is reused when continuing again. Committed trim or output-setting changes make
the previous output unavailable until regenerated; scrubbing and playback do not.
Pending time text blocks use of the old output, and Escape restores the previous
field without forcing regeneration when nothing was committed.

Only a successful apply opens Applied (the internal Done state). Partial failure, cancellation, and failure
stay on Set with the prepared GIF available for Retry and Diagnostics. Success
means the lock-screen files were updated, not that Windows displayed the
animation. The existing optional **Lock now** file-read check remains available
after success and still requires an explicit user choice.

After success, **Applied** is the current breadcrumb. Use **Choose** to select a
replacement, **Edit** to adjust the existing video, or **Set** to apply the prepared
GIF again. The completion page has no duplicate footer actions. Returning to Choose
preserves the draft until a replacement is successfully prepared. Ordinary
navigation back from Diagnostics or Settings resumes the current stage, while
Diagnostics' **Choose source** action opens Choose without discarding the draft.
Drafts are retained in memory only. Conversion/apply disable conflicting actions;
video preparation retains its existing Cancel/Escape behavior.

## Editing the clip

The editor selects source frames, independently of the GIF frame-rate setting. The first and last included frames are shown beside the Start and End fields. End is the boundary immediately after the last included frame; moving End previews that last included frame.

- Start, Play/Pause, and End buttons sit directly below the timeline. Start jumps to the first included frame; End jumps to the last included frame. Both jump buttons are disabled while the selection is playing and become available again when paused. They move only the preview, preserving the trim selection. Click the filmstrip or drag the playhead to preview a source frame. Arrow keys on the focused playhead step exactly one source frame.
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

The encoder explicitly uses the exported PNG dimensions, preserving the chosen
output resolution. Leaving Gifski's dimensions unspecified enables its automatic
downscaling, even when FFmpeg exported full-resolution frames.

Paused previews use the same ordinal frame selection as export, rather than trusting a media-player seek. The editor decodes a small neighborhood and caches up to 32 PNGs for subsequent steps. A live seek is shown while an exact still loads. Preview requests are debounced and cancelled when superseded. Timeline thumbnails are decoded separately. These previews stay in memory and do not share generation's temporary-directory cleanup.

While thumbnails load, the timeline stays black with a spinner and **Loading
thumbnails…**. The overlay ignores input so the playhead and trim handles remain
usable. Success, failure, and cancellation end the loading state, and a stale
request cannot overwrite a newer video's thumbnails or loading indicator.

Indexing and uncached previews decode source video, so long/high-resolution videos can take longer to open or seek. Initial loading displays a page-level overlay outside the scroll area with the filename, the current stage, the discovered frame count, and Cancel (also Escape). Editing is disabled until scanning and opening the media player both finish. Cancellation keeps the previous selection. Uncached frame previews show a smaller spinner over the video while the rest of the editor remains usable; a newer request supersedes the old one.

Metadata/media opening, FFmpeg startup and parsing, and timestamp/PNG processing run on background tasks. Frame-count updates are throttled to five per second, plus initial/final updates. FFmpeg uses at most four decoder/encoder threads, two filter threads, and below-normal process priority to leave CPU headroom for the UI. Cancellation stops the decoder without enumerating a process tree. The regression suite invokes indexing, preview, thumbnails, and export from a simulated UI synchronization context and checks that their processing never posts a continuation back to it.

Reference behavior: [FFmpeg trim and showinfo filters](https://ffmpeg.org/ffmpeg-filters.html#trim), [Gifski presentation timestamp contract](https://github.com/ImageOptim/gifski/blob/main/gifski.h).

## Validation

Run from the repository root:

```powershell
dotnet run --project Tests/MainFlow.Tests/MainFlow.Tests.csproj -c Release
dotnet run --project Tests/VideoEditing.Tests/VideoEditing.Tests.csproj
dotnet run --project Tests/WindowLifecycle.Tests/WindowLifecycle.Tests.csproj -c Release
dotnet run --project Tests/PickedFileWriter.Tests/PickedFileWriter.Tests.csproj -c Release
dotnet build LockScreenGif/LockscreenGif.csproj -p:Platform=x64
```

The integration checks generate local fixtures and use the bundled FFmpeg/Gifski binaries. They verify frame counts, byte-for-byte source/export matches, exclusive end boundaries, one-frame clips, fractional and variable frame rates, exact still previews, thumbnails, reduced-rate boundaries, GIF timing, time parsing, and cancellation. Tests use a unique temporary directory and do not touch lock-screen settings.

Manual UI checks still required when Windows UI automation is available:

Walk both source routes at the default window size, 550×500 minimum, and increased
display scaling. Check fixed footer actions, keyboard focus on each new stage,
Back/Continue reuse, pending invalid text and Escape, and Diagnostics round trips.
Generate, preview, save, apply, retry, and choose a replacement; do not interpret cache
success as observed playback. The flow harness covers navigation, readiness,
stale operation tokens, and outcome classification without modifying Windows.

1. Load `Demos/Video Example.mp4`; confirm the timeline, labels, and thumbnails fit at normal and narrow window widths and increased display scaling. While thumbnails are loading, confirm the black background and spinner are visible and that trimming and seeking still work.
2. Pause, drag each boundary, and release: remain paused on its included boundary frame. Repeat while playing: resume playback. Drag the playhead during playback and verify it resumes at that location within the selection.
3. Step backward and forward, including the first/last source frames and a one-frame selection. Verify button limits, distinct handle hit areas, keyboard focus, and screen-reader range values.
4. Enter invalid, crossed, out-of-range, and hour-long times. Verify Enter/Escape, preserved invalid text while scrubbing, and disabled generation until correction.
5. Try Reset trim, Set start/end to current frame, and the Start, Play/Pause, and End buttons. Confirm Start and End preview the first and last included frames without changing the trim, including for a one-frame selection. Confirm both jump buttons are disabled during playback and enabled again after pausing. Confirm playback loops automatically for both trimmed clips and the full video. Confirm the playhead moves continuously, stays aligned with the video, stops when paused, and jumps directly to the start on looping. Resize during playback, then drag or use arrow keys to confirm the visual marker and hit target remain aligned.
6. Scroll away from the editor and load a replacement video: confirm the loading overlay stays visible, the window remains responsive, and Cancel/Escape restores the previous selection. Confirm repeated frame steps keep the editor responsive and only the latest preview request controls its loading indicator. Generate and save a short GIF and compare its first/last frames to the exact still previews.
7. Close with one X click while a video is paused or playing, after generating/applying it, and after navigating to Settings/Diagnostics. Confirm automatic exit without a media error, including while apply/verification cleanup is pending. Repeat after replacing the video through Choose. Window close detaches the player and its callbacks before disposal; a later page unload must not pause that disposed player. The lifecycle harness covers the managed event orderings; these checks verify native WinUI behavior.
8. Check that the current breadcrumb is highlighted and cannot be clicked or focused as a button. After applying, confirm Applied is highlighted and Choose/Edit/Set provide the corresponding return paths without the old completion buttons.
9. Generate and save a short 1440p clip using Original. Confirm the saved GIF's dimensions match the source and that saving reports success; also overwrite an existing GIF and check a file-provider location. Repeat at a smaller output preset. Encoding regressions cover dimensions, while picker/provider completion also needs native Windows validation.
