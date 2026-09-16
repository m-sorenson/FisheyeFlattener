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
  background thread with a progress bar.
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
   view (shows a progress bar; runs on a background thread so the UI stays
   responsive). Video export includes the original audio track — OpenCV's
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
   Each processed frame is written to a temp PNG, and ffmpeg's concat
   demuxer assembles them using the source's real per-frame gaps
   (`-fps_mode vfr`). Without ffmpeg, export falls back to a single
   constant rate computed as `actual frame count / actual duration` (both
   via ffprobe, since a codec's reported average and OpenCV's own
   frame-count property are frequently just container-metadata estimates
   for real-world compressed video, not a true count). The export-complete
   status message reports the resulting video/audio stream durations and
   their gap, so a mismatch is visible rather than silent.
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

## Packaging as a standalone .exe (optional, not done yet)

`dotnet publish FisheyeFlattener -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`
produces a single-file executable that doesn't require the .NET runtime to
be installed separately. Not set up yet — ask if you want this wired up.
