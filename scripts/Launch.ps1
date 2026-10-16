param([switch]$LAN, [switch]$RenewCert)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$binary = Join-Path $root 'dist\win-x64\AbyssRemote.Host.exe'
$stamp = Join-Path $root 'dist\win-x64\.abyss-source.sha256'
. (Join-Path $PSScriptRoot 'Source-Fingerprint.ps1')
try {
    $fingerprint = Get-AbyssSourceFingerprint -ProjectRoot $root
    $builtFingerprint = if (Test-Path -LiteralPath $stamp) { (Get-Content -LiteralPath $stamp -Raw).Trim() } else { '' }
    if (-not (Test-Path -LiteralPath $binary) -or $fingerprint -ne $builtFingerprint) {
        Write-Host 'Source changed or build stamp missing; rebuilding host...' -ForegroundColor Yellow
        & (Join-Path $PSScriptRoot 'Build-Windows.ps1')
    }
    if (-not (Test-Path $binary)) { throw 'Host executable was not built.' }
    if (-not (Test-Path (Join-Path $root 'dist\win-x64\wwwroot\index.html'))) { throw 'Web UI missing from published output. Re-run BUILD-WINDOWS.bat.' }
    Write-Host "`nOpening Abyss Remote host..." -ForegroundColor Cyan
    $hostArgs = @()
    if ($LAN) { $hostArgs += '--lan' }
    if ($RenewCert) { $hostArgs += '--renew-cert' }
    & $binary @hostArgs
    if ($LASTEXITCODE -ne 0) { throw "Host stopped with exit code $LASTEXITCODE" }
} catch {
    Write-Host "`nERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'See README.md for troubleshooting.'
    Read-Host 'Press Enter to close'
    exit 1
}
