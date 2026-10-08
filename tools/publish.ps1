# Builds the release: tests, self-contained win-x64 app in dist\AeroGatePilot, a portable zip and (if Inno Setup is installed) the installer.
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
$dist = Join-Path $root 'dist'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
# Versioned folder, so a copy that is still running from an older build never blocks a new publish.
$appOut = Join-Path $dist "AeroGatePilot-$version"

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'tests\AeroGatePilot.Tests') -c Release --nologo
    if ($LASTEXITCODE) { throw 'Tests failed.' }
}

if (Test-Path $appOut) { Remove-Item $appOut -Recurse -Force }
dotnet publish (Join-Path $root 'src\AeroGatePilot.App\AeroGatePilot.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false -o $appOut --nologo
if ($LASTEXITCODE) { throw 'Publish failed.' }

# Xray-core next to the app (VLESS / VMess / Trojan / SS). Cached under third_party\xray.
$xrayVer = 'v25.12.8'
$xrayDir = Join-Path $root 'third_party\xray'
$xrayExe = Join-Path $xrayDir 'xray.exe'
if (-not (Test-Path $xrayExe)) {
    Write-Output "Downloading Xray-core $xrayVer…"
    New-Item -ItemType Directory -Force -Path $xrayDir | Out-Null
    $zip = Join-Path $env:TEMP "Xray-windows-64-$xrayVer.zip"
    $url = "https://github.com/XTLS/Xray-core/releases/download/$xrayVer/Xray-windows-64.zip"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $xrayDir -Force
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
}
if (-not (Test-Path $xrayExe)) { throw "xray.exe missing after download ($xrayDir)." }
$coreOut = Join-Path $appOut 'core'
New-Item -ItemType Directory -Force -Path $coreOut | Out-Null
Copy-Item (Join-Path $xrayDir '*') $coreOut -Force
# Keep the folder lean: binary + geo databases + license.
Get-ChildItem $coreOut -File | Where-Object { $_.Name -notmatch '^(xray\.exe|geoip\.dat|geosite\.dat|LICENSE.*)$' } | Remove-Item -Force

$licenses = Join-Path $appOut 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item (Join-Path $root 'third_party\WinDivert-2.2.2-A\LICENSE') (Join-Path $licenses 'WinDivert-LICENSE.txt')
Copy-Item (Join-Path $root 'third_party\fonts\OFL.txt') (Join-Path $licenses 'Vazirmatn-OFL.txt')
if (Test-Path (Join-Path $coreOut 'LICENSE')) {
    Copy-Item (Join-Path $coreOut 'LICENSE') (Join-Path $licenses 'Xray-LICENSE.txt') -Force
}
Copy-Item (Join-Path $root 'README.md') $appOut

$zip = Join-Path $dist "AeroGatePilot-$version-win-x64-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$appOut\*" -DestinationPath $zip
Write-Output "Portable build: $zip"

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($iscc) {
    & $iscc "/DAppVersion=$version" "/DSourceDir=$appOut" "/O$dist" (Join-Path $root 'installer\AeroGatePilot.iss')
    if ($LASTEXITCODE) { throw 'Installer build failed.' }
} else {
    Write-Warning 'Inno Setup 6 not found - skipped the installer (install with: winget install JRSoftware.InnoSetup).'
}
