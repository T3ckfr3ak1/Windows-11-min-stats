$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$appProj = Join-Path $root "TaskbarResourceMonitor\\TaskbarResourceMonitor.csproj"
$publishDir = Join-Path $PSScriptRoot "publish"
$outDir = Join-Path $PSScriptRoot "out"
$wxs = Join-Path $PSScriptRoot "TaskbarResourceMonitor.wxs"
$appIcon = Join-Path $root "TaskbarResourceMonitor\\assets\\app.ico"

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

function Ensure-Wix {
  try {
    wix --version | Out-Null
    return
  } catch {}

  Write-Host "WiX not found. Installing wix tool (per-user)..." -ForegroundColor Yellow
  dotnet tool install --global wix --version 4.*
  $env:PATH = $env:PATH + ";" + (Join-Path $env:USERPROFILE ".dotnet\\tools")
  wix --version | Out-Null
}

Ensure-Wix

Write-Host "Publishing self-contained app for installer..." -ForegroundColor Cyan
dotnet publish $appProj `
  -c Release `
  -r win-x64 `
  -o $publishDir `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:SelfContained=true | Out-Host

if (-not (Test-Path (Join-Path $publishDir "TaskbarResourceMonitor.exe"))) {
  throw "Publish failed: TaskbarResourceMonitor.exe not found in $publishDir"
}

if (Test-Path $appIcon) {
  Copy-Item -Path $appIcon -Destination (Join-Path $publishDir "app.ico") -Force
}

Write-Host "Building MSI..." -ForegroundColor Cyan
wix build $wxs `
  -arch x64 `
  -ext WixToolset.UI.wixext `
  -ext WixToolset.Util.wixext `
  -bindpath "publish=$publishDir" `
  -o (Join-Path $outDir "TaskbarResourceMonitor.msi") | Out-Host

Write-Host "MSI created:" (Join-Path $outDir "TaskbarResourceMonitor.msi") -ForegroundColor Green

