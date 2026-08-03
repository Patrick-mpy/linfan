#requires -RunAsAdministrator
<#
.SYNOPSIS
  Removes the LinFan service + GUI shortcut + files again (counterpart to packaging/uninstall.sh).

.DESCRIPTION
  Stops the service (ramping the fans back to hardware auto in the process), deletes it, removes the
  Start-menu shortcut and the installation directory. The configuration under %ProgramData%\linfan is
  deliberately kept (delete manually if desired).

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

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host '==> stopping & removing the service'
    if ($svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force      # ramps the fans back to hardware auto
        $svc.WaitForStatus('Stopped', '00:00:30')
    }
    & sc.exe delete $ServiceName | Out-Null
}

if (-not $InstallerManaged) {
    $lnk = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\LinFan.lnk'
    Remove-Item -Force -ErrorAction SilentlyContinue $lnk

    Write-Host "==> removing files ($InstallDir)"
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $InstallDir
}

Write-Host "Removed. Configuration stays under $env:ProgramData\linfan (delete manually if desired)."
