# One-command build: unit + integration tests, browser client checks, self-contained
# publish to dist\win-x64, then the published host's own HTTPS self-test.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Step([string]$name) { Write-Host "`n=== $name ===" -ForegroundColor Cyan }

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw 'dotnet is not on PATH. Install the .NET 10 SDK (x64) from https://dotnet.microsoft.com/download/dotnet/10.0' }
$version = & dotnet --version
if ($LASTEXITCODE -ne 0 -or [int]($version.Split('.')[0]) -lt 10) { throw "Found .NET SDK '$version'; version 10 or newer is required." }

Step 'Unit and integration tests'
& dotnet test (Join-Path $root 'tests\AbyssRemote.Tests\AbyssRemote.Tests.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit code $LASTEXITCODE)." }

Step 'Browser client checks'
$node = Get-Command node -ErrorAction SilentlyContinue
if ($node) {
    & node (Join-Path $root 'tests\web\client-checks.mjs')
    if ($LASTEXITCODE -ne 0) { throw "Browser client checks failed (exit code $LASTEXITCODE)." }
} else {
    Write-Host 'Skipped: Node.js is not installed (the .NET tests above still ran).' -ForegroundColor Yellow
}

Step 'Publish'
& (Join-Path $PSScriptRoot 'Build-Windows.ps1')

Step 'HTTPS self-test of the published host'
& (Join-Path $PSScriptRoot 'Smoke-Test.ps1')
