param([switch]$NoSingleFile)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'Source-Fingerprint.ps1')
$project = Join-Path $root 'src\AbyssRemote.Host\AbyssRemote.Host.csproj'
$destination = Join-Path $root 'dist\win-x64'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host 'Abyss Remote needs .NET 10 SDK to build.' -ForegroundColor Yellow
    Write-Host 'Get the SDK from: https://dotnet.microsoft.com/en-us/download/dotnet/10.0'
    throw 'dotnet is not on your PATH. Install .NET 10 SDK (x64), then launch again.'
}
$version = & dotnet --version
if ($LASTEXITCODE -ne 0 -or [int]($version.Split('.')[0]) -lt 10) { throw "Found SDK '$version'; .NET SDK 10 or newer is required." }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Write-Host "Publishing Abyss Remote to $destination" -ForegroundColor Cyan
$args = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $destination, '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
if ($NoSingleFile) { $args = @($args | Where-Object { $_ -ne '-p:PublishSingleFile=true' }) + '-p:PublishSingleFile=false' }
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }
$exe = Join-Path $destination 'AbyssRemote.Host.exe'
if (-not (Test-Path $exe)) { throw "Build exited successfully but executable not found: $exe" }
if (-not (Test-Path (Join-Path $destination 'wwwroot\index.html'))) { throw 'Build incomplete: wwwroot/index.html missing from published output.' }
# Save stamp only after a successful publish, so a failed build cannot launch stale code.
$stamp = Join-Path $destination '.abyss-source.sha256'
(Get-AbyssSourceFingerprint -ProjectRoot $root) | Set-Content -LiteralPath $stamp -NoNewline -Encoding ASCII
Write-Host 'Build completed successfully.' -ForegroundColor Green
Write-Host "Host: $exe"
