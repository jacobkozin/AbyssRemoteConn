# Windows diagnostic: run the self-contained host's native HTTPS smoke test.
# This avoids PowerShell 5.1's TLS callback/runspace pitfalls entirely.
# An authenticated browser connection is NOT required to run this test.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$binary = Join-Path $root 'dist\win-x64\AbyssRemote.Host.exe'
$stamp = Join-Path $root 'dist\win-x64\.abyss-source.sha256'
. (Join-Path $PSScriptRoot 'Source-Fingerprint.ps1')

# Build when absent or stale: tests must never silently execute an outdated binary.
$fingerprint = Get-AbyssSourceFingerprint -ProjectRoot $root
$builtFingerprint = if (Test-Path -LiteralPath $stamp) { (Get-Content -LiteralPath $stamp -Raw).Trim() } else { '' }
if (-not (Test-Path -LiteralPath $binary) -or $builtFingerprint -ne $fingerprint) {
    Write-Host 'Host build is missing or outdated. Building from this release source...' -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'Build-Windows.ps1')
}
if (-not (Test-Path -LiteralPath $binary)) { throw "No host executable found at $binary" }

$listener = Get-NetTCPConnection -LocalPort 8443 -State Listen -ErrorAction SilentlyContinue
if ($listener) {
    $owners = ($listener | Select-Object -ExpandProperty OwningProcess -Unique) -join ', '
    throw "Port 8443 is in use (PID: $owners). Close Abyss Remote or another service on that port, then run TEST-HOST.bat again."
}

Write-Host 'Running in-process .NET HTTPS diagnostics...' -ForegroundColor Cyan
& $binary '--smoke-test'
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) { throw "Abyss Remote self-test failed with exit code $exitCode. The exception immediately above is the actual cause." }
Write-Host 'PASS: Windows startup smoke test finished successfully.' -ForegroundColor Green
