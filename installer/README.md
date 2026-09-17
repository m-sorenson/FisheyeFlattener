# Building the installer

Produces `output/FisheyeFlattener-Setup.exe` — a standalone Windows installer
built with [Inno Setup](https://jrsoftware.org/isinfo.php) (free,
non-commercial use).

## Why self-contained

The installer bundles a **self-contained** publish of the app: the .NET 8
runtime and every native OpenCV DLL are included in the installed files, so
the target machine needs nothing pre-installed for the app itself to run —
no ".NET Desktop Runtime" prerequisite dialog, no separate download. This is
why the publish output is ~240MB and the compressed installer is ~75MB,
rather than the few-MB installer you'd get from a framework-dependent build.

**ffmpeg is not bundled** the same way. It's a much larger, separately-
licensed set of binaries, and the app already works without it — video
export just comes out silent (no audio track) rather than failing. Instead,
the installer offers an optional, unchecked-by-default task, "Install
ffmpeg via winget", that runs `winget install --id Gyan.FFmpeg -e` during
setup if the user opts in. Requires winget and internet access at install
time; skipping it is fully supported, matching how the app already handles
a missing ffmpeg (see the main README's **Workflow** section).

## Steps

1. **Publish** the self-contained build (from the repo root):

   ```powershell
   dotnet publish FisheyeFlattener\FisheyeFlattener.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false
   ```

   Output lands in
   `FisheyeFlattener\bin\Release\net8.0-windows\win-x64\publish\`.

2. **Install Inno Setup** (one-time, if not already installed):

   ```powershell
   winget install --id JRSoftware.InnoSetup -e
   ```

3. **Compile** the installer:

   ```powershell
   & "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\FisheyeFlattener.iss
   ```

   Produces `installer\output\FisheyeFlattener-Setup.exe`.

## What the installer does

- Installs to `%LocalAppData%\Programs\Fisheye Flattener` by default (no
  admin rights required - `PrivilegesRequired=lowest` in the `.iss`
  script), with the option to install for all users if run elevated.
- Start Menu shortcut always; desktop shortcut is an opt-in checkbox.
- Optional "install ffmpeg via winget" checkbox (unchecked by default -
  see above).
- Registers a normal Windows uninstaller (Add/Remove Programs → Fisheye
  Flattener), which removes every installed file.

### Uninstall safeguards (`[Code]` section)

- **Closes the app first if it's running.** Windows locks a running .exe's
  own files, so uninstalling over a live instance would otherwise leave
  files behind (exactly the "Device or resource busy" problem this
  project hit more than once rebuilding over a running dev instance).
  `EnsureMainAppClosed` finds every window titled "Fisheye Flattener"
  (there can legitimately be more than one - two instances launched back
  to back, say) and sends each a `WM_CLOSE`, same as clicking its own X,
  so the app's normal shutdown handling runs rather than a hard kill.
  Interactive uninstall: if it won't close within a few seconds, asks the
  user to close it and Retry rather than killing potentially-unsaved
  state. Silent/unattended uninstall (`/VERYSILENT`, scripted deployment):
  nobody's there to ask, so falls back to `taskkill /F` after the same
  graceful attempt, so an automated uninstall still completes.
- **"Also uninstall ffmpeg" is a real uninstall-time checkbox**, not just
  a yes/no message box - built via `CreateCustomForm`/`TNewCheckBox`
  (Inno Setup 6.6.0+ requires `ClientWidth`/`ClientHeight` up front in
  `CreateCustomForm`'s parameters, not assigned afterward - a change from
  older versions). Only shown if `winget list --id Gyan.FFmpeg -e` exit
  code confirms ffmpeg is actually currently installed (0 = found), and
  skipped entirely for silent uninstalls (nobody to click it - defaults
  to leaving ffmpeg alone, same conservative default as everywhere else
  ffmpeg is concerned). If checked, also waits up to ~10s for any running
  `ffmpeg.exe`/`ffprobe.exe` (e.g. a video export in progress) to finish
  on its own before removing it.

## Verifying a build

Before shipping a new installer, at minimum:

```powershell
# Silent install to a scratch directory
installer\output\FisheyeFlattener-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="" /DIR="C:\some\scratch\path"

# Confirm it actually launches from the installed location
& "C:\some\scratch\path\FisheyeFlattener.exe"

# Confirm uninstalling *closes a running instance* rather than leaving files locked
# (launch the app from that path first, leave it running, then:)
& "C:\some\scratch\path\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
# ...then confirm both the app process is gone AND every file was removed -
# either one alone isn't proof; a partial removal with locked files skipped
# can look "done" if you only check the process, or only check file count.
```

This is exactly how this installer has been verified each time it's
changed - not just "it compiled," but a real silent install, launch, and
uninstall (including, after the close-before-uninstall logic was added,
uninstalling with the app *actively running*).

> **Git Bash / MSYS gotcha**: `/VERYSILENT` and similar single-slash flags
> get silently mangled into a Windows path (e.g. `C:/Program Files/Git/
> VERYSILENT...`) by Git Bash's automatic POSIX-path conversion, which
> makes the installer run *without* actually being silent - it just hangs
> waiting for input nobody sees. Use `//VERYSILENT` (double leading
> slash) from Git Bash to prevent the conversion; this doesn't apply from
> a real PowerShell/cmd prompt. This bit the verification of this exact
> installer once already - if a "silent" test run seems to hang for far
> longer than a normal install/uninstall should take, check the actual
> process command line (`Get-CimInstance Win32_Process -Filter "Name=
> '...'" | Select CommandLine`) before assuming something in the script
> itself is broken.

## Bumping the version

Update `<Version>` in `FisheyeFlattener\FisheyeFlattener.csproj` and
`AppVersion` in `installer\FisheyeFlattener.iss` together when the app
itself changed - they're two separate values today (not wired to read
from one source). When only the installer script changed and the app
binary is identical to what already shipped (e.g. an uninstall-logic
fix), it's fine to bump only `AppVersion` and leave the csproj `Version`
alone, rather than implying an app change that didn't happen.
