# Starts the published host, rebuilding it first if the source changed.
# Extra arguments go to the host, for example:  START-LOCAL-ONLY.bat --port 8555
param(
    [switch]$LAN,
    [switch]$RenewCert,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$HostArgs
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$binary = Join-Path $root 'dist\win-x64\AbyssRemote.Host.exe'
$stamp = Join-Path $root 'dist\win-x64\.abyss-source.sha256'
. (Join-Path $PSScriptRoot 'Source-Fingerprint.ps1')
try {
    $fingerprint = Get-AbyssSourceFingerprint -ProjectRoot $root
    $builtFingerprint = if (Test-Path -LiteralPath $stamp) { (Get-Content -LiteralPath $stamp -Raw).Trim() } else { '' }
    if (-not (Test-Path -LiteralPath $binary) -or $fingerprint -ne $builtFingerprint) {
        Write-Host 'Source changed or never built; building the host first...' -ForegroundColor Yellow
        & (Join-Path $PSScriptRoot 'Build-Windows.ps1')
    }
    $arguments = @()
    if ($LAN) { $arguments += '--lan' }
    if ($RenewCert) { $arguments += '--renew-cert' }
    if ($HostArgs) { $arguments += $HostArgs }
    Write-Host "`nStarting Abyss Remote..." -ForegroundColor Cyan
    & $binary @arguments
    if ($LASTEXITCODE -ne 0) { throw "The host stopped with exit code $LASTEXITCODE (see the message above)." }
} catch {
    Write-Host "`nERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'See README.md (Troubleshooting).'
    Read-Host 'Press Enter to close'
    exit 1
}
