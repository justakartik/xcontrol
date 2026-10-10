<#
.SYNOPSIS  Restores, builds, tests and packages XControl.  Output: dist\XControl-<version>-win-x64.zip
.DESCRIPTION
  Requirements: .NET 10 SDK (https://dotnet.microsoft.com/download/dotnet/10.0) with the Windows desktop workload
  (included in the SDK). If the SDK is in a non-standard place, set DOTNET_ROOT before running.
#>
[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $repo
if (-not $env:DOTNET_ROOT) { $user = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'; if (Test-Path $user) { $env:DOTNET_ROOT = $user; $env:PATH = "$user;$env:PATH" } }
$env:DOTNET_NOLOGO = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

dotnet build XControl.sln -c Release
if ($LASTEXITCODE) { throw "build failed" }
if (-not $SkipTests) { dotnet test tests\XControl.Tests -c Release --no-build; if ($LASTEXITCODE) { throw "tests failed" } }

$version = ([xml](Get-Content src\XControl.App\XControl.App.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$stage = Join-Path $repo "dist\XControl-$version-win-x64"
if (Test-Path $stage) { Get-ChildItem $stage -Recurse -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force "$stage\app","$stage\redist","$stage\docs" | Out-Null

dotnet publish src\XControl.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o "$stage\app"
if ($LASTEXITCODE) { throw "publish failed" }

$drv = Join-Path $repo 'redist\ViGEmBus_1.22.0_x64_x86_arm64.exe'
if (-not (Test-Path $drv)) {
  New-Item -ItemType Directory -Force (Split-Path $drv) | Out-Null
  Invoke-WebRequest 'https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe' -OutFile $drv -UseBasicParsing
}
if ((Get-FileHash $drv -Algorithm SHA256).Hash -ne '89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A') { throw 'ViGEmBus installer hash mismatch' }
Copy-Item $drv "$stage\redist\"
Copy-Item scripts\install.ps1, scripts\uninstall.ps1, README.md, LICENSE -Destination $stage
Copy-Item docs\* "$stage\docs\"
Copy-Item THIRD-PARTY.md $stage

dotnet publish src\XControl.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "$repo\dist\single"
if ($LASTEXITCODE) { throw "single-file publish failed" }
Copy-Item "$repo\dist\single\XControl.exe" (Join-Path $repo "dist\XControl-$version-win-x64.exe") -Force

$zip = Join-Path $repo "dist\XControl-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$h = (Get-FileHash $zip -Algorithm SHA256).Hash
"$h  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256"
Write-Host ("Single-file exe: dist\XControl-$version-win-x64.exe")
Write-Host ("Package: {0} ({1:N0} MB)  SHA-256 {2}" -f $zip, ((Get-Item $zip).Length / 1MB), $h)
