param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$installerDir = Join-Path $PSScriptRoot "Sentinel.Installer"
$agentProject = Join-Path $root "agent\Sentinel\Sentinel.csproj"
$keyProject = Join-Path $root "usbkey\Cargo.toml"
$keyExe = Join-Path $root "usbkey\target\release\sentinel-key.exe"
$agentOut = Join-Path $env:TEMP "sentinel-installer-payload"
$payloadZip = Join-Path $installerDir "payload.zip"
$out = Join-Path $PSScriptRoot "out"

Write-Host "Publishing agent payload..."
if (Test-Path $agentOut) { Remove-Item $agentOut -Recurse -Force }
dotnet publish $agentProject -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $agentOut | Out-Host

$agentExe = Join-Path $agentOut "Sentinel.exe"
if (-not (Test-Path $agentExe)) { throw "agent publish did not produce Sentinel.exe" }

Write-Host "Building Sentinel Key (Rust)..."
Push-Location (Join-Path $root "usbkey")
try {
    cargo build --release --manifest-path $keyProject | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
} finally {
    Pop-Location
}
if (-not (Test-Path $keyExe)) { throw "cargo did not produce sentinel-key.exe" }

Write-Host "Building payload.zip..."
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Compress-Archive -Path $agentExe, $keyExe -DestinationPath $payloadZip -CompressionLevel Optimal

Write-Host "Publishing installer..."
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
Remove-Item (Join-Path $installerDir "obj"), (Join-Path $installerDir "bin") -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $installerDir "Sentinel.Installer.csproj") -c $Configuration -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $out | Out-Host

Get-ChildItem $out | Select-Object Name, Length | Format-Table
Write-Host "Installer: $out\Sentinel Setup.exe"
