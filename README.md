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
   default framing.
5. The preview updates live as you drag or adjust sliders (debounced
   ~60ms). Hit **Export Flattened...** to write the *current view* as a
   full-resolution image, or process an entire video through that same
   view (shows a progress bar; runs on a background thread so the UI stays
   responsive).
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
7. For video, a playback bar appears under the preview — **Play/Pause**,
   a scrub slider, and elapsed/total time. Playback shows the *flattened*
   result live (not the raw fisheye), using whatever view you've dragged to
   and whatever calibration is set. Dragging the scrub bar pauses and
   seeks. Adjusting the view while playing updates the dewarp map for the
   next frame without interrupting playback.

Note: a 360° panorama-unwrap mode existed in an earlier version and is
still in `FisheyeFlattener.Core` (`DewarpMath.BuildPanoramaMap`) but isn't
wired into the UI anymore — the app is single-view/PTZ-only now, since
that's what maps to "drag to look around."

## How it works

The lens is modeled as an equidistant fisheye (`r = f * theta`), the
standard model for this class of lens. `FisheyeFlattener.Core/DewarpMath.cs`
builds a per-pixel remap once per settings change and reuses it across every
video frame (via `Cv2.Remap`) for speed.

## Packaging as a standalone .exe (optional, not done yet)

`dotnet publish FisheyeFlattener -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`
produces a single-file executable that doesn't require the .NET runtime to
be installed separately. Not set up yet — ask if you want this wired up.
