# Only grants access from the Windows 'LocalSubnet' alias, on Private network profiles.
# Run elevated. No public-internet firewall opening or port forwarding.
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Run this script as Administrator. The separate firewall BAT will request elevation.' }
$rule = Get-NetFirewallRule -DisplayName 'Abyss Remote (Local Subnet)' -ErrorAction SilentlyContinue
if ($rule) {
    Write-Host 'Existing Abyss Remote firewall rule found; removing it before creating the restricted replacement.'
    $rule | Remove-NetFirewallRule
}
New-NetFirewallRule -DisplayName 'Abyss Remote (Local Subnet)' `
    -Description 'Allows Abyss Remote only from the local subnet on private Windows network profiles.' `
    -Direction Inbound -Action Allow -Enabled True -Profile Private -Protocol TCP `
    -LocalPort 8443 -RemoteAddress LocalSubnet | Out-Null
Write-Host 'Allowed TCP 8443 on PRIVATE networks, limited to LocalSubnet.' -ForegroundColor Green
Write-Host 'To remove later: Remove-NetFirewallRule -DisplayName "Abyss Remote (Local Subnet)"'
