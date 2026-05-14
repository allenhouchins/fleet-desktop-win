# Authenticode signs a file using one of two backends:
#
#   1. A PFX certificate, base64-encoded, supplied via env vars:
#        WINDOWS_PFX_BASE64
#        WINDOWS_PFX_PASSWORD
#
#   2. Azure Trusted Signing — supplied via env vars:
#        AZURE_TS_ENDPOINT          (e.g. https://eus.codesigning.azure.net/)
#        AZURE_TS_ACCOUNT
#        AZURE_TS_PROFILE
#        AZURE_TENANT_ID
#        AZURE_CLIENT_ID
#        AZURE_CLIENT_SECRET
#
# Detection order: Azure Trusted Signing first (more secure, no PFX leakage),
# then PFX as a fallback. If neither is configured, the script exits with no-op.
#
# Timestamping uses DigiCert's free public RFC 3161 server unless overridden via
# the TIMESTAMP_URL env var.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [string]$Description = "Fleet Desktop",
    [string]$DescriptionUrl = "https://github.com/allenhouchins/fleet-desktop-win"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Path)) {
    throw "File not found: $Path"
}

$TimestampUrl = if ($env:TIMESTAMP_URL) { $env:TIMESTAMP_URL } else { "http://timestamp.digicert.com" }

function Find-SignTool {
    # Try common Windows SDK locations.
    $kits = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
        "${env:ProgramFiles}\Windows Kits\10\bin"
    )
    foreach ($kit in $kits) {
        if (-not (Test-Path $kit)) { continue }
        $candidates = Get-ChildItem $kit -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "x64\\signtool\.exe$" }
        if ($candidates) {
            return ($candidates | Sort-Object -Property FullName -Descending | Select-Object -First 1).FullName
        }
    }
    # PATH fallback.
    $fromPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($fromPath) { return $fromPath.Source }
    throw "signtool.exe not found. Install the Windows SDK or add signtool to PATH."
}

# --- Azure Trusted Signing path ------------------------------------------------
if ($env:AZURE_TS_ENDPOINT -and $env:AZURE_TS_ACCOUNT -and $env:AZURE_TS_PROFILE -and
    $env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_ID -and $env:AZURE_CLIENT_SECRET) {

    Write-Host "    Using Azure Trusted Signing..." -ForegroundColor DarkCyan

    # Ensure the dlib metadata file exists.
    $MetadataPath = Join-Path $env:TEMP "fleet-desktop-ats-metadata.json"
    $metadata = @{
        Endpoint = $env:AZURE_TS_ENDPOINT
        CodeSigningAccountName = $env:AZURE_TS_ACCOUNT
        CertificateProfileName = $env:AZURE_TS_PROFILE
        CorrelationId = [Guid]::NewGuid().ToString()
    } | ConvertTo-Json
    $metadata | Out-File -FilePath $MetadataPath -Encoding utf8

    # Install the dlib if missing.
    $DlibPath = Join-Path $env:USERPROFILE ".azuretrustedsigning\Microsoft.Trusted.Signing.Client.dll"
    if (-not (Test-Path $DlibPath)) {
        Write-Host "    Installing Trusted Signing dlib..." -ForegroundColor DarkCyan
        New-Item -ItemType Directory -Path (Split-Path $DlibPath) -Force | Out-Null
        Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Microsoft.Trusted.Signing.Client" -OutFile "$env:TEMP\ats-client.zip"
        Expand-Archive -Path "$env:TEMP\ats-client.zip" -DestinationPath "$env:TEMP\ats-client" -Force
        Copy-Item -Path "$env:TEMP\ats-client\bin\x64\Microsoft.Trusted.Signing.Client.dll" -Destination $DlibPath -Force
    }

    $SignTool = Find-SignTool
    & $SignTool sign /v `
        /fd SHA256 /td SHA256 /tr $TimestampUrl `
        /dlib $DlibPath /dmdf $MetadataPath `
        /d $Description /du $DescriptionUrl `
        $Path
    if ($LASTEXITCODE -ne 0) { throw "signtool (Azure Trusted Signing) failed with exit code $LASTEXITCODE" }

    Remove-Item $MetadataPath -ErrorAction SilentlyContinue
    Write-Host "    Signed: $Path" -ForegroundColor Green
    exit 0
}

# --- PFX path -----------------------------------------------------------------
if ($env:WINDOWS_PFX_BASE64 -and $env:WINDOWS_PFX_PASSWORD) {
    Write-Host "    Using PFX certificate..." -ForegroundColor DarkCyan

    $PfxPath = Join-Path $env:TEMP "fleet-desktop-signing.pfx"
    [System.IO.File]::WriteAllBytes($PfxPath, [Convert]::FromBase64String($env:WINDOWS_PFX_BASE64))

    try {
        $SignTool = Find-SignTool
        & $SignTool sign /v `
            /fd SHA256 /td SHA256 /tr $TimestampUrl `
            /f $PfxPath /p $env:WINDOWS_PFX_PASSWORD `
            /d $Description /du $DescriptionUrl `
            $Path
        if ($LASTEXITCODE -ne 0) { throw "signtool (PFX) failed with exit code $LASTEXITCODE" }
        Write-Host "    Signed: $Path" -ForegroundColor Green
    } finally {
        Remove-Item $PfxPath -Force -ErrorAction SilentlyContinue
    }
    exit 0
}

Write-Host "    No signing credentials configured — skipping." -ForegroundColor Yellow
exit 0
