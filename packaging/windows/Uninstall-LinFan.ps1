#requires -RunAsAdministrator
<#
.SYNOPSIS
  Removes the LinFan service + GUI shortcut + files again (counterpart to packaging/uninstall.sh).

.DESCRIPTION
  Stops the service (ramping the fans back to hardware auto in the process), deletes it, removes the
  "LinFan Users" IPC access group, the Start-menu shortcut and the installation directory. The
  configuration under %ProgramData%\linfan is deliberately kept (delete manually if desired).

  Call from a PowerShell started as administrator:
    .\Uninstall-LinFan.ps1

.NOTES
  -InstallerManaged is set by the Inno uninstaller: Inno cleans up files + shortcut itself,
  this script then only removes the service.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'LinFan'),
    [switch]$InstallerManaged
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'LinFan'
$IpcGroup = 'LinFan Users'

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host '==> stopping & removing the service'
    if ($svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force      # ramps the fans back to hardware auto
        $svc.WaitForStatus('Stopped', '00:00:30')
    }
    & sc.exe delete $ServiceName | Out-Null
}

# The IPC access group is an artifact of this installation and has no purpose without the daemon -
# remove it in both modes (Inno knows nothing about it). Never fatal: a leftover group is harmless.
try {
    if (Get-LocalGroup -Name $IpcGroup -ErrorAction SilentlyContinue) {
        Write-Host "==> removing the IPC access group '$IpcGroup'"
        Remove-LocalGroup -Name $IpcGroup
    }
}
catch {
    Write-Warning "IPC access group '$IpcGroup' could not be removed: $($_.Exception.Message)"
}

# --- Close a running GUI before anything is deleted: it holds App\*.dll, which would leave a
#     half-removed installation behind. Same terminate-instead-of-asking reasoning as in
#     Install-LinFan.ps1. Done in BOTH modes: in Inno mode this runs before Inno deletes the files,
#     so it protects that path too instead of relying on the Restart Manager reaching an uninstall.
#     Never fatal - a leftover directory is reported below, but must not abort the service removal. ---
$appPattern = Join-Path $InstallDir 'App\*'
$gui = @(Get-Process -Name 'LinFan.App' -ErrorAction SilentlyContinue |
    Where-Object { try { $_.Path -like $appPattern } catch { $false } })
if ($gui) {
    Write-Host '==> closing the running GUI'
    $gui | Stop-Process -Force
    # The file handles are only released when the process really exits - wait for that.
    Wait-Process -InputObject $gui -Timeout 10 -ErrorAction SilentlyContinue
    if (@($gui | Where-Object { -not $_.HasExited })) {
        Write-Warning 'The LinFan GUI is still running; its files cannot be removed. Close it and run the script again.'
    }
}

if (-not $InstallerManaged) {
    $lnk = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\LinFan.lnk'
    Remove-Item -Force -ErrorAction SilentlyContinue $lnk

    Write-Host "==> removing files ($InstallDir)"
    # SilentlyContinue so a single locked file cannot abort the uninstall - but say so afterwards
    # instead of reporting a clean removal that did not happen.
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $InstallDir
    if (Test-Path $InstallDir) {
        Write-Warning "Some files could not be removed - '$InstallDir' still exists. Delete it manually."
    }
}

Write-Host "Removed. Configuration stays under $env:ProgramData\linfan (delete manually if desired)."
