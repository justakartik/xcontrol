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
param([switch]$SkipDriver, [switch]$Yes, [switch]$Download, [switch]$NoOpen)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$appSrc  = Join-Path $root 'app'
$target  = Join-Path $env:LOCALAPPDATA 'Programs\XControl'
$driverExe  = Join-Path $root 'redist\ViGEmBus_1.22.0_x64_x86_arm64.exe'
$driverSha  = '89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A'
$driverSigner = 'Nefarius Software Solutions e.U.'

if (-not (Test-Path (Join-Path $appSrc 'XControl.exe'))) {
  # Most common mistake: running this from GitHub's "Download ZIP" of the SOURCE code instead of the release package.
  Write-Warning "app\XControl.exe is not next to install.ps1 - this looks like the source code, not the release package."
  Write-Host  "The installable package is on the Releases page: https://github.com/justakartik/xcontrol/releases/latest"
  Write-Host  "(download XControl-x.y.z-win-x64.zip, unzip it, and run install.ps1 from inside that folder)."
  if (-not $Download) {
    Write-Host ""
    Write-Host "What to do: on the Releases page download  XControl-x.y.z-win-x64.zip  (NOT 'Source code (zip)'),"
    Write-Host "right-click > Extract All, open the extracted folder, and run install.ps1 from there."
    Write-Host "Or run this script again with -Download and it will fetch the latest release for you."
    if (-not $NoOpen) { try { Start-Process 'https://github.com/justakartik/xcontrol/releases/latest' } catch { } }
    exit 1
  }

  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  $ProgressPreference = 'SilentlyContinue'   # the progress bar makes Invoke-WebRequest ~100x slower in Windows PowerShell 5.1
  $api = 'https://api.github.com/repos/justakartik/xcontrol/releases/latest'
  $rel  = Invoke-RestMethod $api -Headers @{ 'User-Agent' = 'xcontrol-installer' }
  $zipAsset = $rel.assets | Where-Object { $_.name -like 'XControl-*-win-x64.zip' } | Select-Object -First 1
  $sumAsset = $rel.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' } | Select-Object -First 1
  if (-not $zipAsset) { throw "No release package found at $($rel.html_url). Download it manually from the Releases page." }

  $tmp = Join-Path ([IO.Path]::GetTempPath()) ("xcontrol-" + [Guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force $tmp | Out-Null
  $zipPath = Join-Path $tmp $zipAsset.name
  Write-Host "Downloading $($zipAsset.name) ($([math]::Round($zipAsset.size/1MB,1)) MB) from $($rel.tag_name)..."
  Invoke-WebRequest $zipAsset.browser_download_url -OutFile $zipPath -UseBasicParsing

  if ($sumAsset) {
    $sums = (Invoke-WebRequest $sumAsset.browser_download_url -UseBasicParsing).Content
    $line = ($sums -split "`n") | Where-Object { $_ -match [regex]::Escape($zipAsset.name) } | Select-Object -First 1
    $expected = ($line -split '\s+')[0].Trim()
    $actual = (Get-FileHash $zipPath -Algorithm SHA256).Hash
    if ($expected -and $expected -ne $actual) { throw "Downloaded file hash mismatch (expected $expected, got $actual). Not installing." }
    Write-Host "SHA-256 verified."
  } else { Write-Warning "No SHA256SUMS.txt in the release; could not verify the download." }

  Expand-Archive $zipPath -DestinationPath (Join-Path $tmp 'pkg') -Force
  $inner = Join-Path $tmp 'pkg\install.ps1'
  if (-not (Test-Path $inner)) { throw "Release package layout unexpected (no install.ps1 inside)." }
  $fwd = @{}; if ($SkipDriver) { $fwd.SkipDriver = $true }; if ($Yes) { $fwd.Yes = $true }
  & $inner @fwd
  exit $LASTEXITCODE
}

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
