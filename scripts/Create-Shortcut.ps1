# Creates "Abyss Remote.lnk", a shortcut to the published program (dist\win-x64\AbyssRemote.exe).
# CREATE-DESKTOP-SHORTCUT.bat runs it without arguments, which puts the shortcut on the Desktop.
# Another folder works too, for example the Start menu:
#   .\scripts\Create-Shortcut.ps1 -Destination ([Environment]::GetFolderPath('Programs'))
param(
    # The folder that gets the shortcut.
    [string]$Destination = [Environment]::GetFolderPath('Desktop'),
    # The program the shortcut starts. Leave it out to use the published program.
    [string]$Target
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Target) {
    $Target = Join-Path $root 'dist\win-x64\AbyssRemote.exe'
    if (-not (Test-Path -LiteralPath $Target -PathType Leaf)) {
        Write-Host 'The program has not been built yet. Building it first...' -ForegroundColor Yellow
        & (Join-Path $PSScriptRoot 'Build-Windows.ps1')
    }
}
if (-not (Test-Path -LiteralPath $Target -PathType Leaf)) { throw "Program not found: $Target" }
if (-not (Test-Path -LiteralPath $Destination -PathType Container)) { throw "Folder not found: $Destination" }

$Target = (Resolve-Path -LiteralPath $Target).Path
$link = Join-Path (Resolve-Path -LiteralPath $Destination).Path 'Abyss Remote.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($link)
$shortcut.TargetPath = $Target
$shortcut.WorkingDirectory = Split-Path -Parent $Target
$shortcut.IconLocation = "$Target,0"
$shortcut.Description = 'Abyss Remote: see and control this PC from a phone or tablet'
$shortcut.Save()
Write-Host "Created $link" -ForegroundColor Green
