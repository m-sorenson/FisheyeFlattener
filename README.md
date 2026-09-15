# Fisheye Flattener (WPF / C#)

Native Windows desktop app that turns a circular fisheye export from a
Ubiquiti Protect Fisheye camera (G4/G5 Fisheye, etc.) into a normal-looking
flat image or video — either a single "virtual PTZ" perspective view, or a
360° panoramic unwrap.

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
4. Pick a **Mount type** — loads sensible defaults:
   - **Ceiling** (looking down): Panorama unwrap covering the full 360°
     around the room.
   - **Wall** (looking out): Panorama unwrap covering roughly the 180°
     hemisphere in front of the camera.
5. Pick a **Flatten Mode**:
   - **Perspective (single view)** — normal rectilinear "looking at one
     spot" view. Yaw/Pitch aim it, FOV zooms. Pitch near ±90° points right
     at the horizon, where the lens's captured hemisphere runs out —
     expect part of the view to go black there; back off a bit (e.g. -60°).
   - **Panorama (360 unwrap)** — the whole scene unrolled into one wide
     strip.
6. The preview updates live as you drag sliders (debounced ~60ms). Hit
   **Export Flattened...** to write a full-resolution image, or process an
   entire video (shows a progress bar; runs on a background thread so the
   UI stays responsive).

## How it works

The lens is modeled as an equidistant fisheye (`r = f * theta`), the
standard model for this class of lens. `FisheyeFlattener.Core/DewarpMath.cs`
builds a per-pixel remap once per settings change and reuses it across every
video frame (via `Cv2.Remap`) for speed.

## Packaging as a standalone .exe (optional, not done yet)

`dotnet publish FisheyeFlattener -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`
produces a single-file executable that doesn't require the .NET runtime to
be installed separately. Not set up yet — ask if you want this wired up.
