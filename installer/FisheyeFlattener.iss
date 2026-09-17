; Inno Setup script for Fisheye Flattener.
;
; Builds a single Setup.exe that installs the self-contained publish output
; (bundles the .NET runtime + OpenCV native libraries, so the target machine
; needs nothing pre-installed for the app itself) and offers an optional task
; to install ffmpeg via winget - ffmpeg is not bundled (its full builds are
; 100+MB and winget already handles this reliably; the app itself works
; without it, just without audio in exported video, exactly as documented in
; the main README).
;
; Build (after publishing - see BUILD.md in this folder):
;   "C:\Users\<you>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" installer\FisheyeFlattener.iss

#define AppName "Fisheye Flattener"
#define AppVersion "1.0.1"
#define AppPublisher "Mike Sorenson"
#define AppExeName "FisheyeFlattener.exe"
#define PublishDir "..\FisheyeFlattener\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{6E9F0B7B-6C5F-4F7A-9C2E-5B1F0F3B9A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=FisheyeFlattener-Setup
Compression=lzma2
SolidCompression=yes
SetupIconFile=..\FisheyeFlattener\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "installffmpeg"; Description: "Install ffmpeg (needed for audio in exported video; downloads via winget - requires internet access)"; GroupDescription: "Optional dependencies:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; winget lives under the current user's WindowsApps folder - launched via cmd /C
; so Inno Setup can wait on it and show its own "please wait" UI. Not silenced,
; since winget's own progress output is useful if it's slow or hits a prompt.
Filename: "{cmd}"; Parameters: "/C winget install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements"; \
    Description: "Installing ffmpeg via winget (this may take a minute)..."; \
    StatusMsg: "Installing ffmpeg..."; Flags: runascurrentuser waituntilterminated; Tasks: installffmpeg
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
