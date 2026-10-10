<#
.SYNOPSIS  Installs XControl for the current user (no administrator rights needed for the app itself).
.DESCRIPTION
  1. Copies the app to %LOCALAPPDATA%\Programs\XControl and creates a Start Menu shortcut.
  2. Checks for the ViGEmBus virtual-gamepad driver. If it is missing, offers to run the bundled installer.
     The driver install is the ONLY step that needs administrator approval (a Windows UAC prompt appears).
     Before running it, the script verifies the installer's SHA-256 and its Authenticode signature.
  Run from the extracted package folder:   powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
[CmdletBinding()]
param([switch]$SkipDriver, [switch]$Yes)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$appSrc  = Join-Path $root 'app'
$target  = Join-Path $env:LOCALAPPDATA 'Programs\XControl'
$driverExe  = Join-Path $root 'redist\ViGEmBus_1.22.0_x64_x86_arm64.exe'
$driverSha  = '89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A'
$driverSigner = 'Nefarius Software Solutions e.U.'

if (-not (Test-Path (Join-Path $appSrc 'XControl.exe'))) { throw "app\XControl.exe not found next to install.ps1. Run this from the extracted package." }

Write-Host "Installing XControl to $target"
Get-Process XControl -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $appSrc '*') $target -Recurse -Force

$lnkDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut((Join-Path $lnkDir 'XControl.lnk'))
$lnk.TargetPath = Join-Path $target 'XControl.exe'
$lnk.WorkingDirectory = $target
$lnk.Description = 'Keyboard and mouse to virtual Xbox controller'
$lnk.Save()
Write-Host "Start Menu shortcut created."

$driverInstalled = Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\ViGEmBus'
if ($driverInstalled) { Write-Host "ViGEmBus driver: already installed." }
elseif ($SkipDriver) { Write-Warning "ViGEmBus driver is NOT installed (-SkipDriver). XControl cannot create the virtual controller until it is." }
else {
  Write-Host ""
  Write-Host "The virtual controller needs the ViGEmBus kernel driver (Nefarius Software Solutions e.U., Microsoft-signed)."
  Write-Host "NOTE: ViGEmBus is retired upstream (no further updates) but still works. Installing needs administrator approval."
  if (-not (Test-Path $driverExe)) { throw "Driver installer not found: $driverExe" }
  $hash = (Get-FileHash $driverExe -Algorithm SHA256).Hash
  if ($hash -ne $driverSha) { throw "Driver installer hash mismatch ($hash). Refusing to run it." }
  $sig = Get-AuthenticodeSignature $driverExe
  if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch [regex]::Escape($driverSigner)) { throw "Driver installer signature is not valid / not from $driverSigner. Refusing to run it." }
  Write-Host "Installer verified: SHA-256 matches, signature valid ($driverSigner)."
  $go = $Yes
  if (-not $go) { $go = (Read-Host "Run the driver installer now? (y/N)") -match '^(y|yes)$' }
  if ($go) {
    $p = Start-Process $driverExe -ArgumentList '/exenoui','/qn','/norestart' -Verb RunAs -Wait -PassThru
    Write-Host "Driver installer exit code: $($p.ExitCode)"
    if (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\ViGEmBus') { Write-Host "ViGEmBus driver installed." }
    else { Write-Warning "Driver service not found after install. Reboot, then run this script again." }
  } else { Write-Warning "Skipped. Run $driverExe yourself later." }
}

Write-Host ""
Write-Host "Done. Start 'XControl' from the Start Menu. Default toggle shortcut: Ctrl+Shift+F8."
