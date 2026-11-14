# Runs the published program's built-in HTTPS self-test (AbyssRemote.exe --smoke-test).
# The program picks a free loopback port itself, so this works even while Abyss Remote or
# another server is already running.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$binary = Join-Path $root 'dist\win-x64\AbyssRemote.exe'
$stamp = Join-Path $root 'dist\win-x64\.abyss-source.sha256'
. (Join-Path $PSScriptRoot 'Source-Fingerprint.ps1')
. (Join-Path $PSScriptRoot 'Run-Host.ps1')

# Never test an outdated binary: rebuild when the source changed since the last publish.
$fingerprint = Get-AbyssSourceFingerprint -ProjectRoot $root
$builtFingerprint = if (Test-Path -LiteralPath $stamp) { (Get-Content -LiteralPath $stamp -Raw).Trim() } else { '' }
if (-not (Test-Path -LiteralPath $binary) -or $builtFingerprint -ne $fingerprint) {
    Write-Host 'The published program is missing or outdated. Building it first...' -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'Build-Windows.ps1')
}

$exitCode = Invoke-AbyssRemote -Binary $binary -Arguments @('--smoke-test')
if ($exitCode -ne 0) { throw "The self-test failed (exit code $exitCode). The message just above is the cause." }
Write-Host 'PASS: published program self-test.' -ForegroundColor Green
