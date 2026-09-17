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
#define AppVersion "1.0.3"
#define AppPublisher "M-Sorenson"
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

[UninstallDelete]
; The app saves its own preferences (window size/position, which side panels
; are shown, export resolution, volume) to %LocalAppData%\FisheyeFlattener\
; settings.json at runtime - a location and file the [Files] section above has
; no knowledge of, since the app itself creates it, not the installer. Without
; this, uninstalling left it behind - confirmed by an actual install-run-
; uninstall cycle, not assumed: installed, launched the app once (which writes
; this file on close), uninstalled, and found the file untouched while every
; other known location (install dir, Start Menu, desktop shortcut, registry
; uninstall entry) was correctly removed. Just a small preferences file, no
; user content, so removing it unconditionally rather than asking is fine -
; nothing lost on reinstall beyond needing to re-pick window size/volume again.
Type: filesandordirs; Name: "{localappdata}\FisheyeFlattener"

[Run]
; winget lives under the current user's WindowsApps folder - launched via cmd /C
; so Inno Setup can wait on it and show its own "please wait" UI. Not silenced,
; since winget's own progress output is useful if it's slow or hits a prompt.
Filename: "{cmd}"; Parameters: "/C winget install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements"; \
    Description: "Installing ffmpeg via winget (this may take a minute)..."; \
    StatusMsg: "Installing ffmpeg..."; Flags: runascurrentuser waituntilterminated; Tasks: installffmpeg
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  WM_CLOSE = $0010;

var
  FFmpegCheckBox: TNewCheckBox;
  RemoveFFmpegOnUninstall: Boolean;

// True if a process with this image name (e.g. 'ffmpeg.exe') is currently
// running - used for console tools with no window to detect via FindWindow*.
// tasklist prints a matching CSV row only when found; "INFO: No tasks..."
// otherwise, which the Pos() check below correctly treats as not-running.
function IsProcessRunningByName(const ExeName: String): Boolean;
var
  ResultCode: Integer;
  Output: TExecOutput;
  i: Integer;
begin
  Result := False;
  if ExecAndCaptureOutput('tasklist.exe', '/FI "IMAGENAME eq ' + ExeName + '" /FO CSV /NH', '',
     SW_HIDE, ewWaitUntilTerminated, ResultCode, Output) then
  begin
    for i := 0 to GetArrayLength(Output.StdOut) - 1 do
    begin
      if Pos(Lowercase(ExeName), Lowercase(Output.StdOut[i])) > 0 then
      begin
        Result := True;
        Break;
      end;
    end;
  end;
end;

// Asks the app's main window to close (WM_CLOSE - same as clicking its own X,
// so unsaved state/in-progress exports get the app's own normal handling, not
// a hard kill) and waits up to 5s for it to actually exit. Returns whether it's
// closed by the time this returns.
function TryCloseMainAppGracefully(): Boolean;
var
  Wnd: HWND;
  Attempts: Integer;
begin
  // Keeps re-finding and closing whatever window currently matches this title -
  // not just "the first one, once" - so more than one running instance (however
  // that happened) all get asked to close, not just whichever FindWindowByWindowName
  // happened to return first. Bounded so a window that never actually closes
  // (hung, blocked on a dialog) can't loop forever; the caller's own retry/
  // force-kill fallback handles that case.
  Attempts := 0;
  Wnd := FindWindowByWindowName('Fisheye Flattener');
  while (Wnd <> 0) and (Attempts < 40) do
  begin
    PostMessage(Wnd, WM_CLOSE, 0, 0);
    Sleep(100);
    Wnd := FindWindowByWindowName('Fisheye Flattener');
    Attempts := Attempts + 1;
  end;
  Result := Wnd = 0;
end;

// Uninstall will otherwise partially fail (locked .exe/.dll files left behind)
// if the app is still running - exactly the "Device or resource busy" class of
// problem this project has hit before when rebuilding over a running instance.
// Interactive: ask the app to close, and if it won't, tell the user and let them
// retry or cancel the uninstall rather than silently killing potentially
// unsaved state. Silent/unattended (/VERYSILENT, scripted deployment): nobody's
// there to ask, so fall back to a forceful close after the same graceful
// attempt, rather than leaving an automated uninstall half-finished.
function EnsureMainAppClosed(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if FindWindowByWindowName('Fisheye Flattener') = 0 then
    Exit;

  while FindWindowByWindowName('Fisheye Flattener') <> 0 do
  begin
    if TryCloseMainAppGracefully() then
    begin
      Result := True;
      Exit;
    end;

    if UninstallSilent then
    begin
      Exec('taskkill.exe', '/F /IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Result := FindWindowByWindowName('Fisheye Flattener') = 0;
      Exit;
    end;

    if MsgBox('Fisheye Flattener is still running and needs to close before it can be ' +
       'uninstalled.' + #13#10#13#10 + 'Please save any work and close it, then click Retry.',
       mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

// ffmpeg is a shared, machine-wide winget install (not private to this app), so
// it's entirely possible something else on the PC also uses it - a video editor,
// another dev project, a script. There's no reliable way to enumerate every
// possible consumer from here, so default to NOT removing it and say so plainly,
// the same conservative-default pattern as the install-time task above.
function InitializeUninstall(): Boolean;
var
  UninstallForm: TSetupForm;
  InfoText: TNewStaticText;
  OKButton, CancelButton: TNewButton;
  FFmpegDetectResultCode: Integer;
begin
  Result := True;
  RemoveFFmpegOnUninstall := False;

  if not EnsureMainAppClosed() then
  begin
    Result := False;
    Exit;
  end;

  // A silent uninstall (/VERYSILENT - scripted deployment, or an automated test)
  // has nobody present to click the dialog below; showing it anyway would hang
  // forever waiting for input. Skip straight to the safe default (ffmpeg untouched).
  if UninstallSilent then
    Exit;

  // Only offer this if ffmpeg is actually currently installed via winget - no
  // point asking if it was never installed (the setup-time task is opt-in) or
  // was already removed some other way. `winget list --id <id> -e` exits 0 if
  // found, non-zero otherwise (verified directly: 0 when installed, 20 - "No
  // installed package found matching input criteria" - when not; this is more
  // reliable than guessing at winget's install-path layout, which varies by how
  // the package registers itself and doesn't always create a PATH-linked shim).
  if not Exec('cmd.exe', '/C winget list --id Gyan.FFmpeg -e', '', SW_HIDE,
     ewWaitUntilTerminated, FFmpegDetectResultCode) or (FFmpegDetectResultCode <> 0) then
    Exit;

  // Inno Setup 6.6.0+ requires ClientWidth/ClientHeight up front (read-only
  // afterward) rather than assigned as properties post-construction.
  UninstallForm := CreateCustomForm(ScaleX(420), ScaleY(150), False, False);
  try
    UninstallForm.Caption := 'Uninstall Fisheye Flattener';
    UninstallForm.Position := poScreenCenter;
    UninstallForm.BorderStyle := bsDialog;

    InfoText := TNewStaticText.Create(UninstallForm);
    InfoText.Parent := UninstallForm;
    InfoText.Left := ScaleX(16);
    InfoText.Top := ScaleY(16);
    InfoText.Width := UninstallForm.ClientWidth - ScaleX(32);
    InfoText.Height := ScaleY(60);
    InfoText.WordWrap := True;
    InfoText.Caption :=
      'ffmpeg was installed via winget for audio support in exported video. ' +
      'It may also be used by other applications on this PC (video editors, ' +
      'media players, other tools) - only remove it if you''re sure nothing ' +
      'else needs it.';

    FFmpegCheckBox := TNewCheckBox.Create(UninstallForm);
    FFmpegCheckBox.Parent := UninstallForm;
    FFmpegCheckBox.Left := ScaleX(16);
    FFmpegCheckBox.Top := ScaleY(82);
    FFmpegCheckBox.Width := UninstallForm.ClientWidth - ScaleX(32);
    FFmpegCheckBox.Height := ScaleY(17);
    FFmpegCheckBox.Caption := 'Also uninstall ffmpeg';
    FFmpegCheckBox.Checked := False;

    OKButton := TNewButton.Create(UninstallForm);
    OKButton.Parent := UninstallForm;
    OKButton.Width := ScaleX(80);
    OKButton.Height := ScaleY(23);
    OKButton.Left := UninstallForm.ClientWidth - ScaleX(178);
    OKButton.Top := UninstallForm.ClientHeight - ScaleY(35);
    OKButton.Caption := 'Continue';
    OKButton.ModalResult := mrOK;
    OKButton.Default := True;

    CancelButton := TNewButton.Create(UninstallForm);
    CancelButton.Parent := UninstallForm;
    CancelButton.Width := ScaleX(80);
    CancelButton.Height := ScaleY(23);
    CancelButton.Left := UninstallForm.ClientWidth - ScaleX(90);
    CancelButton.Top := UninstallForm.ClientHeight - ScaleY(35);
    CancelButton.Caption := 'Cancel';
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;

    if UninstallForm.ShowModal() = mrCancel then
    begin
      Result := False; // user backed out of the whole uninstall
      Exit;
    end;

    RemoveFFmpegOnUninstall := FFmpegCheckBox.Checked;
  finally
    UninstallForm.Free();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Attempts: Integer;
begin
  if (CurUninstallStep = usPostUninstall) and RemoveFFmpegOnUninstall then
  begin
    // ffmpeg/ffprobe are console tools with no window, spawned per-export and
    // normally short-lived - but if an export happens to be running right this
    // moment (or something else is using this same shared ffmpeg install),
    // yanking it out mid-use is worse than a short wait. Give it up to ~10s to
    // finish on its own before proceeding either way - not worth blocking the
    // uninstall indefinitely over, since Setup's own file-in-use handling is
    // install-side only (see EnsureMainAppClosed above for why that check is
    // separate and handled first).
    Attempts := 0;
    while (IsProcessRunningByName('ffmpeg.exe') or IsProcessRunningByName('ffprobe.exe'))
       and (Attempts < 20) do
    begin
      Sleep(500);
      Attempts := Attempts + 1;
    end;

    Exec('cmd.exe', '/C winget uninstall --id Gyan.FFmpeg -e --silent', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode);
  end;
end;
