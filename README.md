# Fisheye Flattener (WPF / C#)

Native Windows desktop app that turns a circular fisheye export from a
Ubiquiti Protect Fisheye camera (G4/G5 Fisheye, etc.) into a normal-looking
flat image or video, viewed and steered like a virtual PTZ camera — drag
the preview to look around, scroll to zoom.

Protect only lets you export fisheye clips/snapshots in the raw circular
fisheye format; this app does the dewarping afterward, locally, on the
exported file.

This is a rebuild of the original Python/PySide6 prototype (see
`../fisheye-flattener`) as a native WPF app — no Python, no WSL, no
display-forwarding involved. Same dewarp math, ported to C#.

## Project layout

- `FisheyeFlattener.Core` — the dewarp math, calibration, and image/video
  processing, as a plain class library (uses OpenCvSharp4, no WPF
  dependency). Ported line-for-line from the Python `dewarp.py`.
- `FisheyeFlattener` — the WPF UI (`MainWindow`), a reusable
  `NumericSlider` control (label + slider + numeric box, used for every
  calibration/dewarp parameter), file open/export, and video export on a
  background thread with a progress popup. `Assets/AppIcon.ico` is a
  generated (not hand-drawn) multi-resolution icon - a stylized camera
  lens/aperture - used as the app/taskbar/title-bar icon and the system
  tray icon; see `MainWindow.InitializeTrayIcon`.
- `FisheyeFlattener.Tests` — xUnit tests for the Core math, mirroring the
  Python test suite.

## Requirements

- Windows 10/11
- .NET 8 SDK (installed on this machine via `winget install Microsoft.DotNet.SDK.8`)
- ffmpeg, optional (installed on this machine via `winget install Gyan.FFmpeg`) —
  only needed for audio in exported video; without it, video export still
  works, just silent

## Build & run

```powershell
cd FisheyeFlattenerNet
dotnet build FisheyeFlattener.sln
dotnet run --project FisheyeFlattener
```

Or open `FisheyeFlattener.sln` in Visual Studio / VS Code and run from there.

## Tests

```powershell
dotnet test FisheyeFlattener.Tests/FisheyeFlattener.Tests.csproj
```

A system tray icon appears whenever the app is running (double-click it,
or right-click → **Show Fisheye Flattener**, to bring the window to
front; right-click → **Exit** to close the app). It's purely an
indicator/shortcut - closing the main window still exits normally rather
than minimizing to the tray.

A menu bar (**File**, **Edit**, **View**, **About**) sits above the
preview. **File** and **Edit** just give keyboard/menu access to the
same actions as their equivalent toolbar buttons (Open/Export/Exit;
Reset View/Auto-detect Circle/Snap Level) - nothing new there, same
underlying handlers. **View** is the interesting one: **Lens
Calibration** and **Preview Output Settings** (the two right-side
panels, the latter renamed from a plain "View" GroupBox to avoid
colliding with the new View *menu*) are independent, checkable toggles -
each is its own "module" that can be shown or hidden without affecting
the other, collapsing that GroupBox entirely rather than just graying it
out. **About** shows the app name/version.

**Preferences persist** across runs, saved to
`%LocalAppData%\FisheyeFlattener\settings.json` on exit and restored on
next launch: window size/position (clamped so a since-unplugged monitor
can't restore it off-screen), which of the two side panels are shown,
preferred export resolution, and playback volume/mute. **Edit → Reset
Preferences to Defaults** clears all of that back to defaults
immediately (and saves that reset right away, rather than waiting for
the next exit). Deliberately *not* persisted: lens calibration, pan/
tilt/zoom/roll, and the source flip checkboxes - those describe whatever
specific video/image is currently loaded, not a standing preference;
carrying an old file's calibration onto a brand new one would silently
mis-frame it rather than helpfully "remembering" anything.

## Workflow

1. In Protect, export the fisheye clip or snapshot (raw circular fisheye
   image — that's expected).
2. **Open Fisheye File...** — works with images (jpg/png/bmp/tiff) and
   video (mp4/mov/avi/mkv).
3. The app auto-detects the circular lens image (bright circle against the
   black letterboxing Protect exports use). If it picks the wrong circle,
   adjust Center X/Y and Radius by hand, or hit "Auto-detect circle" again.
4. **Look around by dragging the preview** — click and drag to pan/tilt,
   scroll to zoom in/out. This is a virtual PTZ camera: the flattened view
   always looks like a normal photo, and dragging moves what you're looking
   at, like panning a photo viewer. **Reset View** puts it back to the
   default framing. Arrow keys work too (click the preview first to focus
   it).

   Panning (yaw) wraps continuously, so you can drag all the way around
   360°; tilt (pitch) is clamped since past the horizon/nadir extremes
   there's nothing the lens actually captured. This is a *rectilinear*
   (flat-photo) projection, so content still leans more the farther it
   sits from the center of frame (an unavoidable property of any
   flat-photo-style projection at wide field of view — not fixable without
   switching to a curved/cylindrical projection).
5. The preview updates live as you drag or adjust sliders (debounced
   ~60ms). Hit **Export Flattened...** to write the *current view* as a
   full-resolution image, or process an entire video through that same
   view. For video, a separate progress popup (sized to fit its own
   content rather than a guessed fixed height, so its Cancel button can't
   end up clipped off the bottom) tracks export progress with two bars:
   an overall bar spanning the whole export, and a second bar for
   whichever stage is currently running - reading/flattening frames,
   then the ffmpeg step that encodes those frames into the final video,
   then (indeterminate, no per-item progress to report) the audio mux.
   That middle stage - ffmpeg actually encoding every frame - previously
   reported nothing at all once frame reading hit 100%, which looked
   like the export had silently stalled for however long that encode
   took; it's now parsed live from ffmpeg's own `-progress` output, the
   same way frame-reading progress already was. The overall bar's
   per-stage weighting (currently reading 55% / encoding 35% / muxing
   10%) is an approximation, not a measured split - the true ratio
   depends on resolution, encoder, and hardware - chosen so the bar
   moves smoothly across stages rather than claiming precision it
   doesn't have.

   **Cancel** works during the frame-processing phase (video processing
   runs on a background thread so the main window stays responsive and
   usable while it's up), then switches to an **Open Export Location**
   button once done — clicking it opens Explorer with the exported file
   selected. Cancel only works during frame processing; once that's done
   and the ffmpeg encode/mux steps start, the button disables itself
   rather than accept a click that can't do anything (cancelling there
   would mean killing an ffmpeg process mid-write, not implemented).
   Cancelling (or a failure before the mux step) cleans up the
   intermediate video-only temp file rather than leaving it behind.
   Video export includes the original audio track — OpenCV's
   video writer has no audio support at all, so this app writes the
   flattened frames video-only, then shells out to **ffmpeg** (if
   installed) as a second step to copy the original audio onto the result.
   No ffmpeg, or no audio track in the source → you still get a valid
   video, just without audio; the status bar says which happened.

   When ffmpeg is available, export preserves each frame's own original
   timestamp instead of writing the whole file at any single constant
   frame rate — even a very precisely *averaged* one. Motion-triggered
   security footage in particular can have genuinely irregular frame
   timing (not just an imprecise average), which no single constant rate
   can reproduce correctly; it showed up as a real desync at whichever
   specific moment the original timing was uneven, not a uniform drift.
   Each processed frame is written to a temp BMP (not PNG — BMP is a raw
   byte dump with no compression work, which turned out to matter more for
   export speed than the video encoder itself; these are temp files
   deleted right after ffmpeg reads them, so the larger disk footprint
   costs nothing that matters), and ffmpeg's concat demuxer assembles them
   using the source's real per-frame gaps (`-fps_mode vfr`). Without
   ffmpeg, export falls back to a single constant rate computed as `actual
   frame count / actual duration` (both via ffprobe, since a codec's
   reported average and OpenCV's own frame-count property are frequently
   just container-metadata estimates for real-world compressed video, not
   a true count).

   That final assembly step uses GPU hardware encoding when available —
   NVIDIA NVENC, AMD AMF, or Intel QuickSync, tried in that order and
   verified with a real tiny test encode (ffmpeg being *compiled* with
   support doesn't mean the driver actually accepts it), falling back to
   software `libx264` if none work. If a hardware encoder passes that
   quick check but then fails on the real export anyway, it retries once
   with `libx264` automatically rather than losing the export. Measured
   directly on this machine (RTX 5080): switching the temp frame format
   from PNG to BMP was the bigger win (8.36s → ~5.1s for a 20s 1280×720
   clip) — the encoder itself wasn't the dominant cost here, so don't
   assume a GPU alone will fix a slow export if most of the time is spent
   in frame writing instead.
   The export-complete
   status message reports the resulting video/audio stream durations and
   their gap, so a mismatch is visible rather than silent.

   If the source stops providing frames partway through (a decode hiccup
   on one damaged/unusual frame), export seeks a little further ahead and
   retries a few times rather than treating that as the end of the clip.
   If it's still well short of the source's own frame-count estimate after
   that (or a temp frame fails to write at all — disk full, permissions),
   export now fails with a clear error instead of silently producing a
   shorter, truncated video and calling it done.

   Motion-triggered recordings can also have a genuine, large gap between
   two consecutive frames — confirmed on real footage (a Protect Fisheye
   export) three independent ways: OpenCV's own position, ffprobe reading
   the container's raw packet PTS directly, and the camera's own
   burned-in clock overlay, all agreeing on a ~55-second jump between two
   specific, otherwise perfectly good frames (motion detection had simply
   gone quiet for a while, so the camera wasn't recording). This was
   originally reproduced as a literal hold on that one frame, capped at 2
   seconds so it wouldn't be the full 55 — but checked against the actual
   source file, that turned out to be wrong: common players evidently
   don't honor a gap this size as a real-time wait at all (the flashing
   police lights visible in this footage keep flashing continuously
   through the gap when the source plays normally, with no visible
   pause), so even a 2-second hold in the export was a pause the source
   itself never actually shows and looked like a new, self-inflicted
   stutter. Gaps bigger than 2 seconds are now collapsed to a
   near-invisible ~0.1s instead of being displayed at all, matching what
   viewers actually see rather than faithfully reproducing dead recording
   time as a pause nobody asked for. (An earlier version of this fix
   stopped at "cap the hold to 2 seconds," diagnosed by reading the
   user's file directly — ffprobe showed a real `avg_frame_rate` far below
   `r_frame_rate`, confirming genuinely variable timing, and a
   frame-by-frame OpenCV read found the 55033ms gap at a specific frame
   index — but shipping it and comparing side-by-side against the actual
   source revealed the 2-second hold was still visibly wrong.)

   Collapsing a frame's duration shortens the video's total length
   relative to the source by however much was trimmed off a gap — muxing
   the source's *full*, untouched audio track onto that shorter video
   would throw everything after the gap out of sync by exactly that
   amount (the video timeline would run ahead of the audio from that
   point on). Export now builds the video's collapsed per-frame durations
   and the audio's kept time ranges from the exact same values (not two
   independently-derived calculations that could disagree at the edges),
   then uses an ffmpeg `atrim`+`concat` audio filter to trim the source
   audio down to just those kept ranges before muxing — so the audio gets
   the same time compression through a gap as the video, and sync holds
   both before and after every collapsed gap, not just up to the first
   one. Verified with a synthetic pathological case (few frames, one huge
   gap, chosen so even the last frame's own fallback duration — the
   file's overall average interval, used since there's no "next" frame to
   measure a real gap against — would itself exceed the threshold)
   confirming the video and audio totals match to the millisecond, and
   with a real ffmpeg encode/mux confirming the `atrim`+`concat` filter
   syntax itself produces a file with matching video/audio stream
   durations. Ordinary clips with no gap large enough to collapse take a
   simpler, single whole-track audio copy path unchanged from before.
6. **Lens Calibration → Source correction**: flip the raw fisheye frame
   before dewarping. Use this if the camera itself is mounted upside-down
   or mirrored — toggling these automatically re-mirrors your Center X/Y so
   calibration stays correct.
6b. **View → Snap Level**: if a wall/doorframe/edge that should be
   horizontal looks tilted (the camera mount itself is slightly rotated),
   click **Snap Level**, then click two points along that edge. The app
   rotates the view (Roll) so that edge is level — and because panning has
   no roll drift (dragging/yaw/pitch never rotate the horizon), that
   correction holds no matter where you look afterward. There's also a
   manual Roll slider if you'd rather dial it in by eye.
6c. **Keyboard nudge**: click the preview once to focus it, then use the
   **arrow keys** — Up/Down tilts, Left/Right pans, 5° per press (holding a
   key repeats it). Only touches yaw/pitch, same as dragging; leaves
   Roll/Level and zoom untouched.
7. For video, a playback bar appears under the preview — a ▶/⏸ button, a
   scrub slider, elapsed/total time, and a speaker/volume slider. Playback
   shows the *flattened* result live (not the raw fisheye), using whatever
   view you've dragged to and whatever calibration is set. Dragging the
   scrub bar pauses and seeks. Adjusting the view while playing updates the
   dewarp map for the next frame without interrupting playback. Playback
   paces itself against a stopwatch and seeks ahead if it falls behind, so
   speed stays correct even when a frame is expensive to render (large
   resolution, etc.) — without this it would drift slower than real time
   the same way a naive "one frame per timer tick" approach did.

   Audio during playback is a separate `MediaPlayer` (WPF's own, wraps
   Windows Media Foundation) playing the source file's audio track,
   because OpenCV — used for the video frames — has no audio output path
   at all. Video is slaved to wherever audio actually is (its position is
   read every tick and used directly to pick which frame to show), rather
   than each being paced by an independent clock that both merely *target*
   real time — two such clocks drift apart from each other with nothing
   pulling them back together. Audio hardware timing is the one clock
   actually paced by something external and accurate (the sound device),
   so it's the one video follows.

   That sync comparison is done entirely in **time** (OpenCV's own
   millisecond position), never by converting through the file's reported
   FPS. Security camera exports often report an averaged, non-round FPS
   (e.g. 17.909) that doesn't exactly match true frame timing; using it to
   convert "audio seconds elapsed" into "target video frame" compounds a
   small error linearly the longer playback runs, which showed up as a
   gap that kept growing rather than a fixed offset. Frames are also
   written into a single reused `WriteableBitmap` in place rather than
   allocating a new one every frame, and the "catch up when behind" logic
   only hard-seeks once at least ~half a second behind — smaller gaps just
   read forward sequentially, since seeking compressed video means
   decoding forward from the nearest keyframe, which security footage's
   typically long keyframe intervals can make expensive enough to matter
   if triggered on every tick.

Note: a 360° panorama-unwrap mode existed in an earlier version and is
still in `FisheyeFlattener.Core` (`DewarpMath.BuildPanoramaMap`) but isn't
wired into the UI anymore — the app is single-view/PTZ-only now, since
that's what maps to "drag to look around."

## How it works

The lens is modeled as an equidistant fisheye (`r = f * theta`), the
standard model for this class of lens. `FisheyeFlattener.Core/DewarpMath.cs`
builds a per-pixel remap once per settings change and reuses it across every
video frame (via `Cv2.Remap`) for speed.

The perspective (PTZ) view is built from a local tangent-plane (gnomonic)
basis at the (yaw, pitch) view center, rather than composing independent
pitch-then-yaw rotations. That distinction matters: naive Euler-angle
composition is *not* roll-free for a nadir-referenced camera (a ceiling
fisheye) as you pan — a real-world vertical line at fixed azimuth would
project to wildly different output columns as yaw changed, which showed up
as "the image rotates while panning." Verified numerically (a real vertical
line traced across an elevation range landed at a constant output column
for any yaw/pitch) and visually (rendered a synthetic grid and confirmed
verticals stay vertical across a full pan sweep) before shipping the fix;
see `PerspectivePanHasNoRollDriftForRealVerticalLine` in
`FisheyeFlattener.Tests`.

`PitchDeg` is the angle from the lens's optical axis/nadir (0 = straight
down at the fisheye's center, ~90 = at the horizon) — not a tilt offset
from some other reference.

## Security posture

The app makes no network calls at all (no HTTP client, no sockets) - the
entire attack surface is local: the files you open, and the ffmpeg/
ffprobe processes it shells out to. All process invocations pass
arguments via `ProcessStartInfo.ArgumentList` (never a single
interpolated command-line string), which avoids shell/argument
injection regardless of what characters end up in a file path. `dotnet
list package --vulnerable` is clean across all three projects.
Malformed/corrupted input files (verified against a text file renamed
to `.jpg`, random bytes and an empty file renamed to `.mp4`, and a
truncated-but-header-valid `.mp4`) fail with a normal catchable
exception rather than crashing the process - though that's this app's
own handling, not a guarantee about OpenCV/ffmpeg's own native
decoders, which are the realistic attack surface for a deliberately
crafted malicious media file (mitigated by keeping those dependencies
current, not something this app's C# code can fix on its own). Settings
are deserialized with `System.Text.Json` into a fixed POCO (no
polymorphic/arbitrary-type deserialization), and numeric values from a
hand-edited settings file are clamped before use rather than trusted
outright. The app requests no elevated privileges (no manifest, runs
`asInvoker`).

## Packaging as a standalone .exe (optional, not done yet)

`dotnet publish FisheyeFlattener -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`
produces a single-file executable that doesn't require the .NET runtime to
be installed separately. Not set up yet — ask if you want this wired up.
