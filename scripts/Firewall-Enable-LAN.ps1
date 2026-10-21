# Allows Abyss Remote through Windows Firewall ONLY from the local subnet, ONLY on networks
# marked Private. Run elevated (ENABLE-LAN-FIREWALL.bat asks for elevation). It never opens
# anything to the internet, and the host itself also refuses non-local addresses.
param([ValidateRange(1024, 65535)][int]$Port = 8444)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Run this as Administrator. ENABLE-LAN-FIREWALL.bat requests elevation for you.' }

# One rule per port, so changing ports never deletes a rule another program might rely on.
$name = "Abyss Remote (Local Subnet, TCP $Port)"
Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $name `
    -Description 'Allows Abyss Remote only from the local subnet on Private networks.' `
    -Direction Inbound -Action Allow -Enabled True -Profile Private -Protocol TCP `
    -LocalPort $Port -RemoteAddress LocalSubnet | Out-Null
Write-Host "Allowed TCP $Port on PRIVATE networks, from the local subnet only." -ForegroundColor Green
Write-Host "To remove it later (as Administrator): Remove-NetFirewallRule -DisplayName '$name'"

# Versions before 1.0 used port 8443 and a rule without the port in its name. It is left alone
# on purpose (another local server may use 8443); remove it yourself if nothing needs it.
$legacy = Get-NetFirewallRule -DisplayName 'Abyss Remote (Local Subnet)' -ErrorAction SilentlyContinue
if ($legacy) {
    Write-Host "Note: an older rule 'Abyss Remote (Local Subnet)' still allows its old port. If nothing else needs it:" -ForegroundColor Yellow
    Write-Host "      Remove-NetFirewallRule -DisplayName 'Abyss Remote (Local Subnet)'" -ForegroundColor Yellow
}
