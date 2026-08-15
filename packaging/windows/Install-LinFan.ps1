#requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs LinFan as a Windows service (daemon, LocalSystem) + GUI Start-menu entry.

.DESCRIPTION
  Counterpart to packaging/install.sh. Expects a FINISHED cross-published, self-contained
  win-x64 build (subfolders Daemon\ + App\) - nothing is built on the target PC: no .NET SDK
  needed, the runtime is inside the build. Produce the build on the Linux/dev machine:

    dotnet publish -c Release -r win-x64 --self-contained true src/LinFan.Daemon -o artifacts/LinFan-win-x64/Daemon
    dotnet publish -c Release -r win-x64 --self-contained true src/LinFan.App    -o artifacts/LinFan-win-x64/App

  Call from a PowerShell started as administrator:
    .\Install-LinFan.ps1                      # source: ..\..\artifacts\LinFan-win-x64
    .\Install-LinFan.ps1 -Source C:\path\to\build

.NOTES
  The service runs as LocalSystem - writing PWM via LibreHardwareMonitor/WinRing0 needs admin.
  The GUI runs as a normal user and connects over the named pipe \\.\pipe\linfan; access to it is
  restricted to the local group "LinFan Users", which this script creates and adds the GUI user to
  (counterpart to the 'linfan' socket group on Linux - needs a re-login to take effect).
  The configuration lives machine-wide under %ProgramData%\linfan (daemon = sole writer).
  -InstallerManaged is set by the Inno Setup installer: files + shortcut are then managed by Inno,
  this script only registers the service.
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'LinFan'),
    [switch]$InstallerManaged,
    # Set by the Inno installer: path of a marker file to write when the GUI user was NEWLY added to
    # the IPC group (re-login pending). Inno's NeedRestart() checks it and offers a restart instead
    # of the "Launch LinFan" checkbox, which could not connect with the pre-add token anyway.
    [string]$ReloginMarker
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'LinFan'
$DisplayName = 'LinFan Fan Control'
# Local group the daemon's named-pipe DACL grants access to (must match AllowedGroup in
# src/LinFan.Ipc/Transport/NamedPipeServerTransport.cs).
$IpcGroup = 'LinFan Users'

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

# --- Close a running GUI: it holds App\*.dll, and Windows refuses to overwrite a file in use (the
#     copy below would abort with a sharing violation). Terminate instead of asking it to close: a
#     close request is answered by hiding into the tray, and with unsaved editor changes it opens a
#     modal dialog - a hidden installer run would wait there forever. Terminating is safe: the GUI
#     never writes hardware (the daemon is the sole writer and was just stopped -> fans on hardware
#     auto), only unsaved editor changes are lost, exactly as when the system closes the app.
#     Not needed in Inno mode: the Restart Manager has already closed it (CloseApplications in
#     linfan.iss), and nothing is copied there anyway. ---
$guiWasRunning = $false
if (-not $InstallerManaged) {
    # SilentlyContinue: without it a fresh install with no GUI running would abort right here
    # ($ErrorActionPreference = 'Stop'). Reading .Path throws for processes of other users, hence the
    # try. Only instances from $InstallDir count - a dev GUI from a source tree is left alone.
    $appPattern = Join-Path $InstallDir 'App\*'
    $gui = @(Get-Process -Name 'LinFan.App' -ErrorAction SilentlyContinue |
        Where-Object { try { $_.Path -like $appPattern } catch { $false } })
    if ($gui) {
        $guiWasRunning = $true
        Write-Host '==> closing the running GUI (it holds the files about to be replaced)'
        $gui | Stop-Process -Force
        # Stop-Process returns once the kill was requested; the file handles are only released when the
        # process actually exits. Without this wait the copy races the dying process.
        Wait-Process -InputObject $gui -Timeout 10 -ErrorAction SilentlyContinue
        $stuck = @($gui | Where-Object { -not $_.HasExited })
        if ($stuck) {
            throw ("The running LinFan GUI (PID $($stuck[0].Id)) could not be closed. Close it manually " +
                'and run the script again.')
        }
    }
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
#     Note: if the service gives up permanently it stays off - Windows has no firmware auto-reversion
#     like thinkpad_acpi; a hard-killed daemon may then hold the fans at manual PWM. ---
& sc.exe failure $ServiceName reset= 60 actions= restart/3000/restart/3000/restart/3000 | Out-Null

# --- Start-menu shortcut for the GUI (in Inno mode this is handled by Inno via [Icons]) ---
if (-not $InstallerManaged) {
    $lnk = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\LinFan.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = $appExe
    $shortcut.WorkingDirectory = (Split-Path $appExe)
    $shortcut.Description = 'LinFan - fan curves, temperatures & speeds'
    $shortcut.Save()
}

# --- IPC access group (counterpart to the 'linfan' socket group in packaging/install.sh) ---
# The daemon restricts the named pipe to members of this group. Without it the DACL falls back to
# "Authenticated Users" - every local account could then talk to the privileged daemon. Must happen
# BEFORE the service starts: the daemon resolves the group once, when it creates the first pipe.
# Nothing here may abort the installation; the fallback keeps the GUI working either way.
$needRelogin = $false
$guiUser = $null
try {
    if (-not (Get-LocalGroup -Name $IpcGroup -ErrorAction SilentlyContinue)) {
        Write-Host "==> creating the IPC access group '$IpcGroup'"
        # Windows caps a local group's description at 48 characters - a longer one makes New-LocalGroup
        # fail outright and no group is created at all. Keep this string short.
        New-LocalGroup -Name $IpcGroup -Description 'May connect to the LinFan daemon (IPC pipe).' | Out-Null
    }

    # The GUI user, not the elevating admin: under UAC both are the same account, but with "run as
    # different user" only the interactive console user is the right target (like SUDO_USER on Linux).
    $guiUser = (Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue).UserName
    if (-not $guiUser) { $guiUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name }

    $members = @(Get-LocalGroupMember -Group $IpcGroup -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Name })
    if ($members -notcontains $guiUser) {
        Write-Host "    adding $guiUser to '$IpcGroup'"
        Add-LocalGroupMember -Group $IpcGroup -Member $guiUser
        $needRelogin = $true
    }
}
catch {
    Write-Warning ("IPC access group '$IpcGroup' could not be set up: $($_.Exception.Message)`n" +
        "The daemon falls back to 'Authenticated Users' (every local account may talk to it). " +
        "Create the group manually, add your GUI user, then restart the service.")
}

# Best-effort: a failed marker write must not abort the installation (worst case: no restart offer).
if ($needRelogin -and $ReloginMarker) {
    try { Set-Content -Path $ReloginMarker -Value '1' -Encoding ascii } catch {}
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
if ($guiWasRunning) {
    Write-Host ''
    Write-Host '  NOTE: the running GUI was closed for the upgrade - start it again from the Start menu.'
    Write-Host '        It is deliberately not restarted here: launched from this elevated shell it would'
    Write-Host '        inherit admin rights, and the GUI must stay unprivileged.'
}
if ($needRelogin) {
    Write-Host ''
    # Console output stays pure ASCII: Windows PowerShell reads a .ps1 without BOM as ANSI, so a
    # typographic dash would print as mojibake in the installer log.
    Write-Host "  NOTE: '$guiUser' was added to '$IpcGroup'. Windows only applies group membership at"
    Write-Host '        sign-in: LOG OUT AND BACK IN once, otherwise the GUI reports the service as'
    Write-Host '        unreachable even though it is running.'
}
