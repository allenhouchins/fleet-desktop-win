# Local build script: publishes Fleet Desktop and packages it into an MSI.
# Run from a Developer PowerShell or a regular PowerShell on Windows.
#
# Usage:
#   .\build.ps1                         # builds the Release x64 MSI
#   .\build.ps1 -Runtime win-arm64      # builds the Release ARM64 MSI
#   .\build.ps1 -Configuration Debug    # builds a Debug MSI
#   .\build.ps1 -SkipMsi                # publishes the EXE only, skips the MSI

[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    # Target architecture. fleetd ships for both, and so does Fleet Desktop.
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",

    [switch]$SkipMsi
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$AppProj  = Join-Path $RepoRoot "FleetDesktop\FleetDesktop.csproj"
$WixProj  = Join-Path $RepoRoot "Installer\FleetDesktop.Installer.wixproj"

# WiX names platforms without the "win-" prefix (x64, arm64).
$Platform = $Runtime -replace '^win-', ''

# Resolve version from the .csproj <Version> element so the MSI and EXE stay in lockstep.
$xml = [xml](Get-Content $AppProj)
$Version = ($xml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $Version) { throw "Could not read <Version> from $AppProj" }
Write-Host "==> Fleet Desktop v$Version ($Configuration, $Runtime)" -ForegroundColor Cyan

# 1) Publish the WPF app as a self-contained single-file EXE. Single-file, R2R and
#    the native-DLL layout are set in the .csproj so IDE and CI builds agree.
#    The publish directory is recreated from scratch: the installer harvests every
#    file in it, so anything stale from an earlier build would ship too.
$PublishDir = Join-Path $RepoRoot "FleetDesktop\bin\$Configuration\net8.0-windows\$Runtime\publish\"
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
Write-Host "==> Publishing FleetDesktop.exe..." -ForegroundColor Cyan
dotnet publish $AppProj `
    -c $Configuration `
    -r $Runtime `
    -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$ExePath = Join-Path $PublishDir "FleetDesktop.exe"
if (-not (Test-Path $ExePath)) { throw "Expected $ExePath to exist after publish" }
# The installer harvests the whole publish directory, so drop what the app doesn't
# load: symbols are embedded in the EXE, the WebView2 package's IntelliSense XML
# files are documentation, and its runtimes\ folder holds an x86 loader copy the
# single-file EXE never uses (the loader for this runtime sits next to the EXE).
# (-Filter rather than -Include: Windows PowerShell 5.1 ignores -Include when the
# path ends in a separator, which would delete everything here.)
foreach ($pattern in @("*.pdb", "*.xml")) {
    Get-ChildItem -Path $PublishDir -Filter $pattern -File -ErrorAction SilentlyContinue | Remove-Item -Force
}
Remove-Item (Join-Path $PublishDir "runtimes") -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "    EXE: $ExePath" -ForegroundColor Green
Get-ChildItem $PublishDir | ForEach-Object { Write-Host ("         {0,-32} {1,10:N0} bytes" -f $_.Name, $_.Length) }

# 2) Sign the EXE before packaging. sign.ps1 picks Azure Trusted Signing or PFX
#    from the environment and no-ops (exit 0) when neither is configured.
Write-Host "==> Signing FleetDesktop.exe..." -ForegroundColor Cyan
& "$RepoRoot\sign.ps1" -Path $ExePath
if ($LASTEXITCODE -ne 0) { throw "Signing failed" }

if ($SkipMsi) {
    Write-Host "==> Skipping MSI build (-SkipMsi). Done." -ForegroundColor Green
    exit 0
}

# 3) Build the MSI for the matching platform.
Write-Host "==> Building MSI ($Platform)..." -ForegroundColor Cyan
dotnet build $WixProj `
    -c $Configuration `
    -p:Platform=$Platform `
    -p:Version=$Version `
    -p:PublishDir=$PublishDir
if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

$MsiName = "fleet_desktop-v$Version-$Platform.msi"
$MsiPath = Join-Path $RepoRoot "Installer\bin\$Platform\$Configuration\$MsiName"
if (-not (Test-Path $MsiPath)) {
    # WiX may emit without the platform subdir depending on SDK version.
    $MsiPath = Join-Path $RepoRoot "Installer\bin\$Configuration\$MsiName"
}
if (-not (Test-Path $MsiPath)) { throw "MSI build succeeded but expected output file was not found" }
Write-Host "    MSI: $MsiPath" -ForegroundColor Green

# 4) Sign the MSI (same credential detection as step 2).
Write-Host "==> Signing MSI..." -ForegroundColor Cyan
& "$RepoRoot\sign.ps1" -Path $MsiPath
if ($LASTEXITCODE -ne 0) { throw "Signing failed" }

Write-Host ""
Write-Host "==> Done." -ForegroundColor Green
Write-Host "    $MsiPath"

# Output for GitHub Actions
if ($env:GITHUB_OUTPUT) {
    Add-Content -Path $env:GITHUB_OUTPUT -Value "MSI_PATH=$MsiPath"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "MSI_NAME=$MsiName"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "VERSION=$Version"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "PLATFORM=$Platform"
}
