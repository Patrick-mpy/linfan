; Inno Setup script for LinFan (Windows) — produces a one-click installer from the
; cross-published, self-contained win-x64 build.
;
; COMPILE on Windows with Inno Setup 6 (https://jrsoftware.org/isdl.php):
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" packaging\windows\linfan.iss
; Produce the build first (on the Linux/dev machine, cross-publish):
;   dotnet publish -c Release -r win-x64 --self-contained true src/LinFan.Daemon -o artifacts/LinFan-win-x64/Daemon
;   dotnet publish -c Release -r win-x64 --self-contained true src/LinFan.App    -o artifacts/LinFan-win-x64/App
;
; The service registration is handled by the PowerShell scripts (single source of truth for the
; service logic); Inno only places the files, calls the scripts, and manages the shortcuts and
; the optional GUI autostart.

#define AppName "LinFan"
; Fallback for manual builds; the release pipeline injects the version from the git tag
; via ISCC /DAppVersion=X.Y.Z (single source of truth = git tag).
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppPublisher "LinFan"
; Source of the published artifacts, relative to this script (packaging\windows\ -> repo root\artifacts).
#define PayloadDir "..\..\artifacts\LinFan-win-x64"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\..\artifacts
OutputBaseFilename=LinFan-Setup-{#AppVersion}-win-x64
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
; Writing PWM + registering the service need elevated rights.
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName} Fan Control
; The service scripts run hidden, so their console hints never reach the user — the re-login needed
; for the "LinFan Users" group membership is shown on this page instead.
InfoAfterFile=PostInstall.txt

[Tasks]
; Checked by default: the GUI starts hidden in the tray (--minimized), so the login stays quiet.
Name: "autostart"; Description: "Start {#AppName} automatically at login (minimized to tray)"

[Files]
Source: "{#PayloadDir}\Daemon\*"; DestDir: "{app}\Daemon"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#PayloadDir}\App\*";    DestDir: "{app}\App";    Flags: recursesubdirs createallsubdirs ignoreversion
Source: "Install-LinFan.ps1";     DestDir: "{app}"; Flags: ignoreversion
Source: "Uninstall-LinFan.ps1";   DestDir: "{app}"; Flags: ignoreversion

[Icons]
; GUI as a normal user; the uninstaller removes this shortcut again automatically.
Name: "{group}\LinFan"; Filename: "{app}\App\LinFan.App.exe"; WorkingDir: "{app}\App"; Comment: "Fan curves, temperatures & speeds"
Name: "{group}\Uninstall LinFan"; Filename: "{uninstallexe}"

[Registry]
; Machine-wide autostart (HKLM Run) — the installer runs elevated, so a HKCU entry would land in the
; ADMIN's hive, not the installing user's. The uninstaller removes the value (uninsdeletevalue).
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\App\LinFan.App.exe"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; Register + start the service (files are already in {app} -> -InstallerManaged).
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Install-LinFan.ps1"" -InstallDir ""{app}"" -InstallerManaged -ReloginMarker ""{tmp}\relogin-required.flag"""; StatusMsg: "Registering and starting the LinFan service …"; Flags: runhidden waituntilterminated
; Finish page "Launch LinFan": runasoriginaluser is essential — without it the GUI would inherit the
; installer's elevation, and the GUI must run unprivileged (the service does the privileged work).
Filename: "{app}\App\LinFan.App.exe"; WorkingDir: "{app}\App"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
; Remove the service cleanly (stop -> fans to hardware auto) before Inno deletes the files.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Uninstall-LinFan.ps1"" -InstallDir ""{app}"" -InstallerManaged"; RunOnceId: "RemoveLinFanService"; Flags: runhidden waituntilterminated

[Code]
// Is the service running? Via the exit code of 'sc query | find "RUNNING"' (0 = match = running),
// so it works without capturing stdout. Absent/stopped -> not running.
function ServiceRunning(): Boolean;
var
  rc: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'), '/c sc query LinFan | find "RUNNING" >nul 2>&1',
    '', SW_HIDE, ewWaitUntilTerminated, rc) and (rc = 0);
end;

// First install: the service script just added the GUI user to "LinFan Users", and Windows only
// applies group membership at sign-in — until then the pipe stays unreachable for the GUI. The
// script signals that via the marker file; offering Inno's restart prompt here also suppresses the
// "Launch LinFan" postinstall checkbox (it could not connect with the pre-add token anyway).
// Upgrades (user already a member -> no marker) keep the launch checkbox and skip the prompt.
function NeedRestart(): Boolean;
begin
  Result := FileExists(ExpandConstant('{tmp}\relogin-required.flag'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  rc: Integer;
begin
  Result := '';
  // Before copying: stop a running service, otherwise LinFan.Daemon.exe is in use and the upgrade
  // aborts with a sharing error. 'net stop' waits until the stop is done (fans -> auto).
  // Absent/already stopped -> no stop needed. If the stop fails (timeout, service hangs), the
  // fan-controlling daemon is still running: then ABORT setup instead of overwriting an in-use EXE.
  if ServiceRunning() then
  begin
    Exec(ExpandConstant('{sys}\net.exe'), 'stop LinFan /y', '', SW_HIDE, ewWaitUntilTerminated, rc);
    if ServiceRunning() then
      Result := 'The running LinFan service could not be stopped. Please stop it manually '
        + '(services.msc or "net stop LinFan") and start the setup again.';
  end;
end;
