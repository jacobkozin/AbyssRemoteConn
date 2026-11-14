# AbyssRemote.exe is a windowed program: Windows gives it no console of its own, and PowerShell
# neither waits for such a program nor sets $LASTEXITCODE for it. The modes that print and
# return an exit code (--smoke-test, --probe-video, --console) are started through this helper.
# Dot-source it, like Source-Fingerprint.ps1.
#
# -NoNewWindow hands the program this console (or the pipe a CI job logs to); the program
#  connects to it by itself. -Wait waits until it is done. -PassThru gives us the exit code.
function Invoke-AbyssRemote {
    param(
        [Parameter(Mandatory = $true)][string]$Binary,
        [string[]]$Arguments = @()
    )
    $start = @{ FilePath = $Binary; NoNewWindow = $true; Wait = $true; PassThru = $true }
    # Start-Process joins the list with spaces, so an argument that contains a space gets quotes.
    $quoted = @($Arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
    if ($quoted.Count -gt 0) { $start.ArgumentList = $quoted }
    $process = Start-Process @start
    return $process.ExitCode
}
