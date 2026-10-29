# Build (if needed) and show a toast, then print the exit code.
#
# Usage:
#   .\dev-notify.ps1 [-Url] <https url> [-Runtime win-x64|win-arm64]
#
# Pass a real device page URL, or any https page implementing the fleetDesktop bridge
# (see ToastWindow.xaml.cs for the message contract).
#
# The process is started through System.Diagnostics.Process and waited on directly.
# PowerShell's own operators get this wrong in both directions: the call operator
# doesn't wait for GUI-subsystem executables at all, and `Start-Process -Wait` waits
# for every descendant too - including the detached child that owns the toast - so
# it would only return once the toast closes.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Url,

    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",

    # Remaining arguments pass straight through to notify.
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest
)

$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Bin = Join-Path $RepoRoot "FleetDesktop\bin\Release\net8.0-windows\$Runtime\publish\FleetDesktop.exe"
if (-not (Test-Path $Bin)) {
    & "$RepoRoot\build.ps1" -Runtime $Runtime -SkipMsi
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Quotes arguments for ProcessStartInfo.Arguments (ArgumentList needs PowerShell 7).
function ConvertTo-ArgumentString {
    param([string[]]$Arguments)
    return ($Arguments | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Bin
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.Arguments = ConvertTo-ArgumentString (@("notify", "--url", $Url) + $Rest)
$proc = [System.Diagnostics.Process]::Start($psi)
$stdout = $proc.StandardOutput.ReadToEnd()
$stderr = $proc.StandardError.ReadToEnd()
$proc.WaitForExit()
if ($stdout) { Write-Host $stdout.TrimEnd() }
if ($stderr) { Write-Host $stderr.TrimEnd() }
Write-Host "exit=$($proc.ExitCode)"
exit $proc.ExitCode
