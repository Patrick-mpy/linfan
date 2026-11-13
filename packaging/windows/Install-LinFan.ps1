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
    .\Install-LinFan.ps1                      # source: the release ZIP this script came in
    .\Install-LinFan.ps1 -Source C:\path\to\build

  Without -Source the build is taken from next to this script (Daemon\ + App\, the release ZIP
  layout) or, in a repo checkout, from ..\..\artifacts\LinFan-win-x64 - never from anywhere else.

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
# Local group the daemon's named-pipe DACL grants access to (must match DefaultAllowedGroup in
# src/LinFan.Ipc/Transport/NamedPipeServerTransport.cs).
$IpcGroup = 'LinFan Users'

$daemonExe = Join-Path $InstallDir 'Daemon\LinFan.Daemon.exe'
$appExe = Join-Path $InstallDir 'App\LinFan.App.exe'

# --- Check the source (manual mode only; Inno has already placed the files) ---
if (-not $InstallerManaged) {
    if (-not $Source) {
        # Only two places qualify: the release ZIP (script next to Daemon\ + App\) or a repo checkout.
        # A bare ..\..\artifacts used to resolve to C:\artifacts\... when the ZIP was unpacked directly
        # under C:\ - a folder any user may create, whose content would then run as LocalSystem.
        $repoRoot = Join-Path $PSScriptRoot '..\..'
        if (Test-Path (Join-Path $PSScriptRoot 'Daemon')) {
            $Source = $PSScriptRoot
        }
        elseif (Test-Path (Join-Path $repoRoot 'LinFan.sln')) {
            $Source = Join-Path $repoRoot 'artifacts\LinFan-win-x64'
        }
        else {
            throw "No build next to this script and no repo checkout around it - pass -Source."
        }
    }
    if (Test-Path -LiteralPath $Source) { $Source = (Resolve-Path -LiteralPath $Source).Path }
    Write-Host "==> source: $Source"

    foreach ($sub in @('Daemon', 'App')) {
        $p = Join-Path $Source $sub
        if (-not (Get-ChildItem -Path $p -Filter '*.exe' -ErrorAction SilentlyContinue)) {
            throw "No build found under '$p'. Cross-publish first (see the script header)."
        }
    }

    # The source becomes a LocalSystem service: refuse a folder another non-admin account owns.
    $trustedOwners = @(
        'S-1-5-18', 'S-1-5-32-544', [Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    foreach ($dir in @($Source, (Join-Path $Source 'Daemon'), (Join-Path $Source 'App'))) {
        $owner = (Get-Acl -LiteralPath $dir).GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($trustedOwners -notcontains $owner) {
            throw ("'$dir' belongs to $owner, not to you or the administrators - refusing to install " +
                'from it. Copy the build to a folder you or the administrators own, or use the Setup.exe.')
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

# --- Failure restart, modeled on systemd Restart=on-failure / RestartSec=3. The SCM repeats the last
#     action for every failure past the third ("Subsequent failures"), so a crashing service is restarted
#     every 3 s and never given up; the failure counter is reset after 60 s without a failure.
#     The restart is what ends a hard kill: Windows has no firmware auto-reversion like thinkpad_acpi, so
#     the fans stay at the dead daemon's manual PWM until the next start takes them over. ---
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
# The daemon restricts the named pipe to members of this group. Without it the pipe only admits
# SYSTEM and the administrators - the fans keep being controlled, but the GUI cannot connect. Must
# happen BEFORE the service starts: the daemon resolves the group once, when it creates the first pipe.
# Nothing here may abort the installation: the service has to run either way.
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
        "The GUI cannot connect to the service until the group exists. " +
        "Create the group manually, add your GUI user, then restart the service.")
}

# --- Lock down the data folder (config, logs, LHM restore markers). A new folder under %ProgramData%
#     lets every user create files and subfolders in it, and the LocalSystem daemon writes there: a
#     planted config.json.tmp used to be adopted along with its owner, and a planted junction or
#     symlink would redirect the daemon's writes - or this script's icacls, which follows links.
#     Admins and SYSTEM get full control, users read only. Before the service starts, so the daemon
#     never writes into the old, open folder. ---
function Invoke-Icacls {
    & icacls.exe @args | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls $($args -join ' ') exited with $LASTEXITCODE" }
}

function Test-Link($item) { [bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint) }

# Deletes without following links: .NET removes a link itself, and 'rd /s' never descends into a
# junction - Windows PowerShell 5.1's Remove-Item -Recurse can delete a junction's TARGET content.
function Remove-Entry($item) {
    Write-Warning "removing '$($item.FullName)' from the LinFan data folder (not written by LinFan)"
    if (Test-Link $item) {
        if ($item.PSIsContainer) { [IO.Directory]::Delete($item.FullName) } else { [IO.File]::Delete($item.FullName) }
    }
    elseif ($item.PSIsContainer) { & cmd.exe /c rd /s /q "`"$($item.FullName)`"" | Out-Null }
    else { [IO.File]::Delete($item.FullName) }
}

$dataDir = Join-Path $env:ProgramData 'linfan'
try {
    $existingDir = Get-Item -LiteralPath $dataDir -Force -ErrorAction SilentlyContinue
    if ($existingDir -and ((Test-Link $existingDir) -or -not $existingDir.PSIsContainer)) {
        Remove-Entry $existingDir
    }
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

    # Close the folder itself first, without recursing: from here on only admins can add entries,
    # so nothing can be planted between the clean-up below and the recursive pass after it.
    # SIDs, not names: the group names are localized ("Administratoren", "Benutzer").
    Invoke-Icacls $dataDir /setowner '*S-1-5-32-544' /Q
    Invoke-Icacls $dataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' `
        '*S-1-5-32-545:(OI)(CI)RX' /Q

    # Keep only what LinFan itself writes there - as real files and a real logs folder.
    foreach ($item in @(Get-ChildItem -LiteralPath $dataDir -Force)) {
        $expected = if ($item.PSIsContainer) { $item.Name -eq 'logs' }
                    else { $item.Name -eq 'config.json' -or $item.Name -like 'config.json.invalid*' -or
                           $item.Name -like 'lhm-restore.*.marker' }
        if ((Test-Link $item) -or -not $expected) { Remove-Entry $item }
    }
    $logDir = Join-Path $dataDir 'logs'
    if (Test-Path -LiteralPath $logDir) {
        foreach ($item in @(Get-ChildItem -LiteralPath $logDir -Force)) {
            if ((Test-Link $item) -or $item.PSIsContainer -or $item.Name -notlike 'linfan.log*') {
                Remove-Entry $item
            }
        }
    }

    # No links left, so the recursive passes stay inside the folder. /reset drops explicit entries
    # someone added to existing files; they inherit the folder's ACL from now on.
    Invoke-Icacls $dataDir /setowner '*S-1-5-32-544' /T /C /Q
    if (Get-ChildItem -LiteralPath $dataDir -Force) {
        Invoke-Icacls (Join-Path $dataDir '*') /reset /T /C /Q
    }
}
catch {
    Write-Warning ("The data folder '$dataDir' could not be locked down completely: $($_.Exception.Message)`n" +
        'Other local users may be able to plant files the service writes to.')
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
    Write-Host '        sign-in: LOG OUT AND BACK IN once, otherwise the GUI reports that access to the'
    Write-Host '        service is denied even though it is running.'
}
