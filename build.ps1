# Local build script: publishes Fleet Desktop and packages it into an MSI.
# Run from a Developer PowerShell or a regular PowerShell on Windows.
#
# Usage:
#   .\build.ps1                       # builds Release MSI
#   .\build.ps1 -Configuration Debug  # builds Debug MSI
#   .\build.ps1 -SkipMsi              # publishes EXE only, skips MSI

[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$SkipMsi
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$AppProj  = Join-Path $RepoRoot "FleetDesktop\FleetDesktop.csproj"
$WixProj  = Join-Path $RepoRoot "Installer\FleetDesktop.Installer.wixproj"

# Resolve version from the .csproj <Version> element so the MSI and EXE stay in lockstep.
$xml = [xml](Get-Content $AppProj)
$Version = ($xml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $Version) { throw "Could not read <Version> from $AppProj" }
Write-Host "==> Fleet Desktop v$Version ($Configuration)" -ForegroundColor Cyan

# 1) Publish the WPF app as a self-contained single-file EXE.
Write-Host "==> Publishing FleetDesktop.exe..." -ForegroundColor Cyan
dotnet publish $AppProj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$PublishDir = Join-Path $RepoRoot "FleetDesktop\bin\$Configuration\net8.0-windows\win-x64\publish\"
$ExePath = Join-Path $PublishDir "FleetDesktop.exe"
if (-not (Test-Path $ExePath)) { throw "Expected $ExePath to exist after publish" }
Write-Host "    EXE: $ExePath" -ForegroundColor Green

# 2) Optionally sign the EXE before packaging.
if ($env:WINDOWS_PFX_BASE64 -and $env:WINDOWS_PFX_PASSWORD) {
    Write-Host "==> Signing FleetDesktop.exe (PFX from env)..." -ForegroundColor Cyan
    & "$RepoRoot\sign.ps1" -Path $ExePath
    if ($LASTEXITCODE -ne 0) { throw "Signing failed" }
} else {
    Write-Host "    (skipping EXE signing — no WINDOWS_PFX_BASE64 / WINDOWS_PFX_PASSWORD set)" -ForegroundColor Yellow
}

if ($SkipMsi) {
    Write-Host "==> Skipping MSI build (--SkipMsi). Done." -ForegroundColor Green
    exit 0
}

# 3) Build the MSI.
Write-Host "==> Building MSI..." -ForegroundColor Cyan
dotnet build $WixProj `
    -c $Configuration `
    -p:Version=$Version `
    -p:PublishDir=$PublishDir
if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

$MsiPath = Join-Path $RepoRoot "Installer\bin\$Configuration\fleet_desktop-v$Version.msi"
if (-not (Test-Path $MsiPath)) {
    # WiX may emit under x64 subdir depending on platform setup.
    $MsiPath = Join-Path $RepoRoot "Installer\bin\x64\$Configuration\fleet_desktop-v$Version.msi"
}
if (-not (Test-Path $MsiPath)) { throw "MSI build succeeded but expected output file was not found" }
Write-Host "    MSI: $MsiPath" -ForegroundColor Green

# 4) Optionally sign the MSI.
if ($env:WINDOWS_PFX_BASE64 -and $env:WINDOWS_PFX_PASSWORD) {
    Write-Host "==> Signing MSI..." -ForegroundColor Cyan
    & "$RepoRoot\sign.ps1" -Path $MsiPath
    if ($LASTEXITCODE -ne 0) { throw "Signing failed" }
} else {
    Write-Host "    (skipping MSI signing — no WINDOWS_PFX_BASE64 / WINDOWS_PFX_PASSWORD set)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "==> Done." -ForegroundColor Green
Write-Host "    $MsiPath"

# Output for GitHub Actions
if ($env:GITHUB_OUTPUT) {
    Add-Content -Path $env:GITHUB_OUTPUT -Value "MSI_PATH=$MsiPath"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "MSI_NAME=$(Split-Path -Leaf $MsiPath)"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "VERSION=$Version"
}
