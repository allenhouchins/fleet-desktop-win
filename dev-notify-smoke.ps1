# Assert the notify exit-code contract.
#
# Only covers cases that exit before a window is created, so it needs no Fleet
# server - which makes it safe to run on any Windows box with WebView2. The paths
# that put a toast on screen have to be checked by hand; see dev-notify.ps1.
#
# Usage:
#   .\dev-notify-smoke.ps1 [-Bin <path to FleetDesktop.exe>] [-Runtime win-x64|win-arm64]

[CmdletBinding()]
param(
    [string]$Bin,

    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64"
)

$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Bin) {
    $Bin = Join-Path $RepoRoot "FleetDesktop\bin\Release\net8.0-windows\$Runtime\publish\FleetDesktop.exe"
}
if (-not (Test-Path $Bin)) {
    & "$RepoRoot\build.ps1" -Runtime $Runtime -SkipMsi
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$failures = 0

# Quotes arguments for ProcessStartInfo.Arguments (ArgumentList needs PowerShell 7).
function ConvertTo-ArgumentString {
    param([string[]]$Arguments)
    return ($Arguments | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
}

# Waits on the process itself, not its descendants: `Start-Process -Wait` would also
# wait for the detached child that owns a toast (see dev-notify.ps1).
function Invoke-FleetDesktop {
    param([string[]]$Arguments)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Bin
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.Arguments = ConvertTo-ArgumentString $Arguments
    $proc = [System.Diagnostics.Process]::Start($psi)
    $output = $proc.StandardOutput.ReadToEnd() + $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()
    return @{ Code = $proc.ExitCode; Output = ($output -split "`n" | Select-Object -First 1) }
}

function Expect {
    param([int]$Want, [string]$Description, [string[]]$Arguments)
    $result = Invoke-FleetDesktop -Arguments $Arguments
    if ($result.Code -eq $Want) {
        Write-Host ("ok   {0,-34} exit={1}" -f $Description, $result.Code)
    } else {
        Write-Host ("FAIL {0,-34} want={1} got={2}`n     {3}" -f $Description, $Want, $result.Code, $result.Output)
        $script:failures++
    }
}

Expect 2 "notify without a url"      @("notify")
Expect 2 "url missing its value"     @("notify", "--url")
Expect 2 "http url"                  @("notify", "--url", "http://example.com")
Expect 2 "host-less url"             @("notify", "--url", "https://")
Expect 2 "not a url"                 @("notify", "--url", "not a url")
Expect 2 "unknown option"            @("notify", "--url", "https://example.com", "--nope")
Expect 2 "unknown subcommand"        @("frobnicate")
Expect 2 "child without pipe"        @("notify", "--url", "https://example.com", "--detached-child")
Expect 2 "handle is not a number"    @("notify", "--url", "https://example.com", "--detached-child", "--handshake-handle", "abc")
Expect 2 "negative handle"           @("notify", "--url", "https://example.com", "--detached-child", "--handshake-handle", "-1")
Expect 0 "help"                      @("help")
Expect 0 "--help"                    @("--help")

# Stand in for a toast already on screen: hold the session-local toast lock. The lock
# check comes before any window is created.
$lock = New-Object System.Threading.Mutex($false, "Local\FleetDesktop.notify.lock")
if ($lock.WaitOne(0)) {
    try {
        Expect 50 "another toast displayed" @("notify", "--url", "https://example.com")
    } finally {
        $lock.ReleaseMutex()
    }
} else {
    Write-Host "skip another toast displayed (lock already held by a real toast)"
}
$lock.Dispose()

# A connection refusal exercises the whole parent/child path (spawn, handshake,
# WebView2 navigation failure) without showing anything.
Expect 30 "unreachable server"       @("notify", "--url", "https://127.0.0.1:9/")

Write-Host ""
if ($failures -eq 0) {
    Write-Host "all checks passed"
    exit 0
} else {
    Write-Host "$failures check(s) failed"
    exit 1
}
