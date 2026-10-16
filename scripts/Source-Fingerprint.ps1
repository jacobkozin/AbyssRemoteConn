# Deterministic fingerprint of the editable host source, excluding compiler outputs.
# Dot-source from Build-Windows.ps1 and Launch.ps1.
function Get-AbyssSourceFingerprint {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)
    $sourceRoot = Join-Path $ProjectRoot 'src\AbyssRemote.Host'
    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) { throw "Source directory is missing: $sourceRoot" }
    $files = @(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Where-Object {
        $_.FullName -notmatch '[\\/](obj|bin)[\\/]'
    } | Sort-Object FullName)
    if ($files.Count -lt 5) { throw 'The source folder is incomplete; refusing to use a stale binary.' }
    $entries = @(
        foreach ($file in $files) {
            $relative = ($file.FullName.Substring($sourceRoot.Length) -replace '^[\\/]+', '').Replace('\', '/')
            "$relative $((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)"
        }
    )
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes(($entries -join "`n"))
        return [System.BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}
