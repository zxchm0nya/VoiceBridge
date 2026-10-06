# VoiceBridge build script: publishes the app and bundles it into an installer.
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$app = Join-Path $root "VoiceBridge\VoiceBridge.csproj"
$inst = Join-Path $root "VoiceBridge.Installer\VoiceBridge.Installer.csproj"
$publishDir = Join-Path $root "publish\win-x64"
$dist = Join-Path $root "dist"

# MSBuild incremental tracking goes stale in this environment and silently
# reuses old outputs (resulting in installers with outdated app binaries).
Write-Host "==> Cleaning previous build outputs..." -ForegroundColor Cyan
foreach ($d in @("publish", "VoiceBridge\bin", "VoiceBridge\obj", "VoiceBridge.Installer\bin", "VoiceBridge.Installer\obj")) {
    $p = Join-Path $root $d
    if (Test-Path $p) { Remove-Item $p -Recurse -Force }  # loud on purpose: a lock must fail the build, not go stale
}

$pubExeCheck = Join-Path $root "publish\win-x64\VoiceBridge.exe"
$runningApp = Get-Process -Name "VoiceBridge" -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $pubExeCheck }
if ($runningApp) { throw "VoiceBridge is running from $pubExeCheck (PID $($runningApp.Id)). Close the app before building." }

Write-Host "==> Building app (Release)..." -ForegroundColor Cyan
dotnet publish $app -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o $publishDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw "App publish failed with exit code $LASTEXITCODE" }

$appExe = Join-Path $publishDir "VoiceBridge.exe"
if (-not (Test-Path $appExe)) { throw "App publish failed" }
Write-Host "    App: $appExe" -ForegroundColor Green

$resourcesDir = Join-Path $root "VoiceBridge.Installer\Resources"
$resExe = Join-Path $resourcesDir "VoiceBridge.exe"
New-Item -ItemType Directory -Path $resourcesDir -Force | Out-Null
for ($attempt = 1; $attempt -le 8; $attempt++) {
    try {
        Copy-Item $appExe $resExe -Force
        break
    }
    catch {
        if ($attempt -eq 8) { throw }
        Start-Sleep -Milliseconds (250 * $attempt)
    }
}

Write-Host "==> Building installer (Release)..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$setupDir = Join-Path $dist "setup"
dotnet publish $inst -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o $setupDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed with exit code $LASTEXITCODE" }

$setupExe = Join-Path $setupDir "VoiceBridgeSetup.exe"
if (-not (Test-Path $setupExe)) { throw "Installer publish failed" }

$finalSetup = Join-Path $dist "VoiceBridge-Setup-1.0.0.exe"
Copy-Item $setupExe $finalSetup -Force
Remove-Item $setupDir -Recurse -Force

Write-Host ""
Write-Host "==============================" -ForegroundColor Cyan
Write-Host " DONE" -ForegroundColor Green
Write-Host "   App      : $appExe" -ForegroundColor Green
Write-Host "   Installer: $finalSetup" -ForegroundColor Green
Write-Host "==============================" -ForegroundColor Cyan
