# Publishes Abyss Remote to dist\win-x64\AbyssRemote.exe.
# Default: a small program that runs on the .NET 10 runtime installed on this PC. Nothing is
# downloaded, because the project has no packages of its own.
# -SelfContained: a standalone program with the .NET runtime inside it (about 135 MB), for PCs
# without .NET. The first such build fetches Microsoft's runtime packs from nuget.org. The
# GitHub build uses it.
param([switch]$NoSingleFile, [switch]$SelfContained)
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
# The program was called AbyssRemote.Host.exe before it became a windowed app; do not leave the old one behind.
Get-ChildItem -LiteralPath $destination -Filter 'AbyssRemote.Host.*' -File | Remove-Item -Force
Write-Host "Publishing Abyss Remote to $destination" -ForegroundColor Cyan
if ($SelfContained) {
    Write-Host 'Standalone build: the .NET runtime goes inside the program.' -ForegroundColor Cyan
    $args = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $destination, '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
} else {
    # The program will use the runtime that is installed here, so make sure it is.
    $runtimes = (& dotnet --list-runtimes) -join "`n"
    foreach ($name in 'Microsoft.NETCore.App', 'Microsoft.AspNetCore.App', 'Microsoft.WindowsDesktop.App') {
        if ($runtimes -notmatch "(?m)^$([regex]::Escape($name)) 10\.") {
            throw "The .NET 10 runtime part '$name' is not installed on this PC. Install the .NET 10 SDK (or Desktop Runtime plus ASP.NET Core Runtime), or build a standalone program instead: BUILD-WINDOWS.bat -SelfContained"
        }
    }
    # The single-file analyzer comes from a NuGet package; without it this build needs no download.
    $args = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '-p:SelfContained=false', '-o', $destination, '-p:PublishSingleFile=true', '-p:EnableSingleFileAnalyzer=false')
}
if ($NoSingleFile) { $args = @($args | Where-Object { $_ -ne '-p:PublishSingleFile=true' }) + '-p:PublishSingleFile=false' }
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }
$exe = Join-Path $destination 'AbyssRemote.exe'
if (-not (Test-Path $exe)) { throw "Build exited successfully but executable not found: $exe" }
if (-not (Test-Path (Join-Path $destination 'wwwroot\index.html'))) { throw 'Build incomplete: wwwroot/index.html missing from published output.' }
# Save stamp only after a successful publish, so a failed build cannot launch stale code.
$stamp = Join-Path $destination '.abyss-source.sha256'
(Get-AbyssSourceFingerprint -ProjectRoot $root) | Set-Content -LiteralPath $stamp -NoNewline -Encoding ASCII
Write-Host 'Build completed successfully.' -ForegroundColor Green
Write-Host "Program: $exe"
