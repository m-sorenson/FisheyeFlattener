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

## Verifying a build

Before shipping a new installer, at minimum:

```powershell
# Silent install to a scratch directory
installer\output\FisheyeFlattener-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="" /DIR="C:\some\scratch\path"

# Confirm it actually launches from the installed location
& "C:\some\scratch\path\FisheyeFlattener.exe"

# Confirm the uninstaller removes everything
& "C:\some\scratch\path\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

This is exactly how the first build of this installer was verified - not
just "it compiled," but a real silent install, launch, and uninstall.

## Bumping the version

Update `<Version>` in `FisheyeFlattener\FisheyeFlattener.csproj` and
`AppVersion` in `installer\FisheyeFlattener.iss` together - they're two
separate values today (not wired to read from one source), so a version
bump means editing both.
