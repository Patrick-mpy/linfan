#requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs LinFan as a Windows service (daemon, LocalSystem) + GUI Start-menu entry.

.DESCRIPTION
  Counterpart to packaging/install.sh. Expects a FINISHED cross-published, self-contained
  win-x64 build (subfolders Daemon\ + App\) — nothing is built on the target PC: no .NET SDK
  needed, the runtime is inside the build. Produce the build on the Linux/dev machine:

    dotnet publish -c Release -r win-x64 --self-contained true src/LinFan.Daemon -o artifacts/LinFan-win-x64/Daemon
    dotnet publish -c Release -r win-x64 --self-contained true src/LinFan.App    -o artifacts/LinFan-win-x64/App

  Call from a PowerShell started as administrator:
    .\Install-LinFan.ps1                      # source: ..\..\artifacts\LinFan-win-x64
    .\Install-LinFan.ps1 -Source C:\path\to\build

.NOTES
  The service runs as LocalSystem — writing PWM via LibreHardwareMonitor/WinRing0 needs admin.
  The GUI runs as a normal user and connects over the named pipe \\.\pipe\linfan.
  The configuration lives machine-wide under %ProgramData%\linfan (daemon = sole writer).
  -InstallerManaged is set by the Inno Setup installer: files + shortcut are then managed by Inno,
  this script only registers the service.
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'LinFan'),
    [switch]$InstallerManaged
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'LinFan'
$DisplayName = 'LinFan Fan Control'

$daemonExe = Join-Path $InstallDir 'Daemon\LinFan.Daemon.exe'
$appExe = Join-Path $InstallDir 'App\LinFan.App.exe'

# --- Check the source (manual mode only; Inno has already placed the files) ---
if (-not $InstallerManaged) {
    if (-not $Source) { $Source = Join-Path $PSScriptRoot '..\..\artifacts\LinFan-win-x64' }
    foreach ($sub in @('Daemon', 'App')) {
        $p = Join-Path $Source $sub
        if (-not (Get-ChildItem -Path $p -Filter '*.exe' -ErrorAction SilentlyContinue)) {
            throw "No build found under '$p'. Cross-publish first (see the script header)."
        }
    }
}

# --- Stop a running service first: overwriting an in-use LinFan.Daemon.exe would abort with a
#     sharing error. The stop ramps the fans cleanly back to hardware auto. ---
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host '==> stopping the existing service (safe upgrade path)'
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus('Stopped', '00:00:30')
}

# --- Copy the files (manual mode) ---
if (-not $InstallerManaged) {
    Write-Host "==> copying the build to $InstallDir"
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -Recurse -Force (Join-Path $Source 'Daemon') $InstallDir
    Copy-Item -Recurse -Force (Join-Path $Source 'App') $InstallDir
}

# --- Register the service or update its path ---
# binPath: quotes because of spaces in the path; 'run' starts the generic-host control loop.
if ($existing) {
    Write-Host '==> updating the service'
    & sc.exe config $ServiceName binPath= "`"$daemonExe`" run" start= auto obj= LocalSystem | Out-Null
}
else {
    Write-Host '==> registering the service (LocalSystem, autostart)'
    New-Service -Name $ServiceName -BinaryPathName "`"$daemonExe`" run" `
        -DisplayName $DisplayName -StartupType Automatic `
        -Description 'Privileged LinFan daemon: control loop + IPC server (named pipe). Writes PWM via LibreHardwareMonitor.' | Out-Null
}

# --- Failure restart, modeled on systemd Restart=on-failure / RestartSec=3. The failure counter is
#     reset after 60 s without a failure (analogous to systemd's StartLimitIntervalSec ~10 s), so only a
#     tight crash loop gives up and isolated, far-apart hiccups do NOT accumulate.
#     Note: if the service gives up permanently it stays off — Windows has no firmware auto-reversion
#     like thinkpad_acpi; a hard-killed daemon may then hold the fans at manual PWM. ---
& sc.exe failure $ServiceName reset= 60 actions= restart/3000/restart/3000/restart/3000 | Out-Null

# --- Start-menu shortcut for the GUI (in Inno mode this is handled by Inno via [Icons]) ---
if (-not $InstallerManaged) {
    $lnk = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\LinFan.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = $appExe
    $shortcut.WorkingDirectory = (Split-Path $appExe)
    $shortcut.Description = 'LinFan — fan curves, temperatures & speeds'
    $shortcut.Save()
}

# --- Start the service ---
Write-Host '==> starting the service'
Start-Service -Name $ServiceName
Get-Service -Name $ServiceName | Format-Table -AutoSize

Write-Host ''
Write-Host 'Done.'
Write-Host "  GUI:     Start menu -> 'LinFan'  (start as a normal user, NOT as admin)"
Write-Host "  Service: Get-Service LinFan   |   Stop: Stop-Service LinFan"
Write-Host "  Config:  $env:ProgramData\linfan\config.json   (daemon is the sole writer)"
