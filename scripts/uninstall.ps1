<#
.SYNOPSIS  Removes XControl. Optionally removes your settings and the ViGEmBus driver.
.DESCRIPTION
  Default: removes the app, Start Menu shortcut and the "run at startup" entry. Settings/profiles are kept.
    -RemoveSettings  also deletes %APPDATA%\XControl (profiles, settings, logs).
    -RemoveDriver    also uninstalls the ViGEmBus driver (administrator approval required; other tools such as
                     DS4Windows or Steam Input helpers may depend on it - only remove it if nothing else uses it).
#>
[CmdletBinding()]
param([switch]$RemoveSettings, [switch]$RemoveDriver)

$ErrorActionPreference = 'Stop'
Get-Process XControl -ErrorAction SilentlyContinue | Stop-Process -Force   # engine releases the virtual pad on exit

$target = Join-Path $env:LOCALAPPDATA 'Programs\XControl'
if (Test-Path $target) { Remove-Item $target -Recurse -Force; Write-Host "Removed $target" }
$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\XControl.lnk'
if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Host "Removed Start Menu shortcut" }
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'XControl' -ErrorAction SilentlyContinue
Write-Host "Removed 'run at startup' entry (if present)."

if ($RemoveSettings) {
  $cfg = Join-Path $env:APPDATA 'XControl'
  if (Test-Path $cfg) { Remove-Item $cfg -Recurse -Force; Write-Host "Removed $cfg" }
} else { Write-Host "Settings kept in $env:APPDATA\XControl (use -RemoveSettings to delete)." }

if ($RemoveDriver) {
  $entry = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
           Where-Object { $_.DisplayName -eq 'ViGEm Bus Driver' } | Select-Object -First 1
  if (-not $entry) { Write-Host "ViGEm Bus Driver is not installed." }
  elseif ($entry.UninstallString -match '\{[0-9A-Fa-f-]{36}\}') {
    $guid = $Matches[0]
    Write-Host "Uninstalling ViGEm Bus Driver $($entry.DisplayVersion) (administrator approval required)..."
    $p = Start-Process msiexec.exe -ArgumentList "/x $guid /passive /norestart" -Verb RunAs -Wait -PassThru
    Write-Host "msiexec exit code: $($p.ExitCode) (3010 = reboot needed)"
  } else { Write-Warning "Unexpected uninstall string; remove 'ViGEm Bus Driver' from Settings > Apps instead." }
} else { Write-Host "ViGEmBus driver left installed (use -RemoveDriver, or Settings > Apps > 'ViGEm Bus Driver')." }
