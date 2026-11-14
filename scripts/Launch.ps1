# Starts the published program, rebuilding it first if the source changed.
# Extra arguments go to the program, for example:  START-LOCAL-ONLY.bat --port 8555
#
# Normally the Abyss Remote window opens and this script is done. With --console the classic
# console screen runs right here until Q is pressed:  START-LOCAL-ONLY.bat --console
param(
    [switch]$LAN,
    [switch]$RenewCert,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$HostArgs
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$binary = Join-Path $root 'dist\win-x64\AbyssRemote.exe'
$stamp = Join-Path $root 'dist\win-x64\.abyss-source.sha256'
. (Join-Path $PSScriptRoot 'Source-Fingerprint.ps1')
. (Join-Path $PSScriptRoot 'Run-Host.ps1')
try {
    $fingerprint = Get-AbyssSourceFingerprint -ProjectRoot $root
    $builtFingerprint = if (Test-Path -LiteralPath $stamp) { (Get-Content -LiteralPath $stamp -Raw).Trim() } else { '' }
    if (-not (Test-Path -LiteralPath $binary) -or $fingerprint -ne $builtFingerprint) {
        Write-Host 'Source changed or never built; building the program first...' -ForegroundColor Yellow
        & (Join-Path $PSScriptRoot 'Build-Windows.ps1')
    }
    # The launcher's name decides the mode for this run, whatever is saved in the window's settings.
    $arguments = @()
    if ($LAN) { $arguments += '--lan' } elseif ($HostArgs -notcontains '--lan') { $arguments += '--local' }
    if ($RenewCert) { $arguments += '--renew-cert' }
    if ($HostArgs) { $arguments += $HostArgs }

    $printsHere = @($arguments | Where-Object { $_ -in '--console', '--smoke-test', '--probe-video' }).Count -gt 0
    if ($printsHere) {
        Write-Host "`nStarting Abyss Remote..." -ForegroundColor Cyan
        $exitCode = Invoke-AbyssRemote -Binary $binary -Arguments $arguments
        if ($exitCode -ne 0) { throw "Abyss Remote stopped with exit code $exitCode (see the message above)." }
    } else {
        Start-Process -FilePath $binary -ArgumentList $arguments
        Write-Host "`nAbyss Remote is starting. Look for its window, or for its icon next to the clock." -ForegroundColor Cyan
    }
} catch {
    Write-Host "`nERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'See README.md (Troubleshooting).'
    Read-Host 'Press Enter to close'
    exit 1
}
