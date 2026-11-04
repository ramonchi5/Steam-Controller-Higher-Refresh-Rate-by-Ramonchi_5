param(
    [string]$Version = "v1.0.1"
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$pkgName  = "Steam-Controller-Higher-Refresh-Rate-by-Ramonchi_5-$Version"
$relRoot  = Join-Path $repoRoot "releases"
$stageDir = Join-Path $env:TEMP "opk-pkg-$Version"
$pkgDir   = Join-Path $stageDir $pkgName
$zipPath  = Join-Path $relRoot "$pkgName.zip"

if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
New-Item -ItemType Directory -Force -Path $pkgDir, $relRoot | Out-Null

$pubRefresh = Join-Path $stageDir "pub-refresh"
$pubAgent   = Join-Path $stageDir "pub-agent"

Write-Host "Publishing self-contained trimmed win-x64 executables..."
& dotnet publish (Join-Path $repoRoot "src\SteamControllerRefreshRate\SteamControllerRefreshRate.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:InvariantGlobalization=true -p:DebugType=None `
    -o $pubRefresh --nologo

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for sc-refresh" }

& dotnet publish (Join-Path $repoRoot "src\SteamControllerBoostAgent\SteamControllerBoostAgent.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:InvariantGlobalization=true -p:DebugType=None `
    -o $pubAgent --nologo

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for sc-boost-agent" }

$binDir     = Join-Path $pkgDir "bin"
$noticesDir = Join-Path $binDir "libxsm3-notices"
$fwMdkDir   = Join-Path $pkgDir "Firmware-UF2\1-GeeekPi-Makerdiary-MDK-Dongle"
$fwStdDir   = Join-Path $pkgDir "Firmware-UF2\2-SuperMini-ProMicro-nRF52840"
New-Item -ItemType Directory -Force -Path $binDir, $noticesDir, $fwMdkDir, $fwStdDir | Out-Null

Copy-Item (Join-Path $pubRefresh "sc-refresh.exe") (Join-Path $binDir "sc-refresh.exe") -Force
Copy-Item (Join-Path $pubAgent "sc-boost-agent.exe") (Join-Path $binDir "sc-boost-agent.exe") -Force
Copy-Item (Join-Path $repoRoot "firmware\openpuck-highrate\OpenPuck\src\libxsm3\LICENSE.txt") (Join-Path $noticesDir "LICENSE.txt") -Force
Copy-Item (Join-Path $repoRoot "firmware\openpuck-highrate\OpenPuck\src\libxsm3\NOTICE.md") (Join-Path $noticesDir "NOTICE.md") -Force

foreach ($f in @("README.md", "LICENSE", "THIRD_PARTY_NOTICES.md")) {
    Copy-Item (Join-Path $repoRoot $f) (Join-Path $pkgDir $f) -Force
}

$fwRel = Join-Path $repoRoot "firmware\releases"
foreach ($f in @("OpenPuck-MDK-FirstInstall-1000Hz.uf2", "OpenPuck-MDK-1000Hz.uf2", "OpenPuck-MDK-500Hz.uf2")) {
    Copy-Item (Join-Path $fwRel $f) (Join-Path $fwMdkDir $f) -Force
}
foreach ($f in @("OpenPuck-1Ctrl-1000Hz.uf2", "OpenPuck-1Ctrl-500Hz.uf2")) {
    Copy-Item (Join-Path $fwRel $f) (Join-Path $fwStdDir $f) -Force
}

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

[System.IO.File]::WriteAllText((Join-Path $pkgDir "Firmware-UF2\WHICH_UF2_FILE_TO_USE.txt"), @"
WHICH .UF2 FIRMWARE FILE SHOULD I DRAG ONTO MY DONGLE?
======================================================

1) GeeekPi / Makerdiary nRF52840 MDK USB Dongle (white/black stick with case):
   - FIRST-TIME INSTALL (factory dongle):
       1-GeeekPi-Makerdiary-MDK-Dongle\OpenPuck-MDK-FirstInstall-1000Hz.uf2
   - UPDATING AN ALREADY-FLASHED MDK DONGLE (keeps your controller pairing!):
       1-GeeekPi-Makerdiary-MDK-Dongle\OpenPuck-MDK-1000Hz.uf2
       (or OpenPuck-MDK-500Hz.uf2 as a 500 Hz fallback)

2) SuperMini nRF52840 / Pro Micro nRF52840 / nice!nano v2:
   - FIRST-TIME INSTALL OR UPDATE:
       2-SuperMini-ProMicro-nRF52840\OpenPuck-1Ctrl-1000Hz.uf2
       (or OpenPuck-1Ctrl-500Hz.uf2 as a 500 Hz fallback)

See README.md in the parent folder for step-by-step pairing and Steam Input 1000 Hz setup!
"@, $utf8NoBom)

[System.IO.File]::WriteAllText((Join-Path $pkgDir "Enable-Persistent-1000Hz.cmd"), @"
@echo off
setlocal
cd /d "%~dp0"
echo Applying 1000 Hz Steam Input + 0.5 ms Windows Kernel Timer Booster...
"bin\sc-refresh.exe" enable-persistent --restart-steam
echo.
"bin\sc-refresh.exe" status
echo.
pause
"@, $utf8NoBom)

[System.IO.File]::WriteAllText((Join-Path $pkgDir "Disable-All-Optimizations.cmd"), @"
@echo off
setlocal
cd /d "%~dp0"
echo Reverting Steam Input to 500 Hz default and removing Windows Startup Booster...
"bin\sc-refresh.exe" disable-persistent --restart-steam
echo.
"bin\sc-refresh.exe" status
echo.
pause
"@, $utf8NoBom)

[System.IO.File]::WriteAllText((Join-Path $pkgDir "Check-Status.cmd"), @"
@echo off
setlocal
cd /d "%~dp0"
"bin\sc-refresh.exe" status
echo.
pause
"@, $utf8NoBom)

[System.IO.File]::WriteAllText((Join-Path $pkgDir "Run-Benchmark.cmd"), @"
@echo off
setlocal
cd /d "%~dp0"
"bin\sc-refresh.exe" benchmark --seconds 5
echo.
pause
"@, $utf8NoBom)

if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stageDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item -Recurse -Force $stageDir

$zipItem = Get-Item $zipPath
Write-Host ("Built release archive: {0} ({1:N2} MB)" -f $zipItem.FullName, ($zipItem.Length / 1MB))
