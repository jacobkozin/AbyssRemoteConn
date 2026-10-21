# Allows Abyss Remote through Windows Firewall ONLY from the local subnet, ONLY on networks
# marked Private. Run elevated (ENABLE-LAN-FIREWALL.bat asks for elevation). It never opens
# anything to the internet, and the host itself also refuses non-local addresses.
param([ValidateRange(1024, 65535)][int]$Port = 8444)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Run this as Administrator. ENABLE-LAN-FIREWALL.bat requests elevation for you.' }
$name = 'Abyss Remote (Local Subnet)'
$existing = Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue
if ($existing) {
    $oldPorts = ($existing | Get-NetFirewallPortFilter | Select-Object -ExpandProperty LocalPort) -join ', '
    Write-Host "Replacing the existing '$name' rule (it allowed port $oldPorts)."
    $existing | Remove-NetFirewallRule
}
New-NetFirewallRule -DisplayName $name `
    -Description 'Allows Abyss Remote only from the local subnet on Private networks.' `
    -Direction Inbound -Action Allow -Enabled True -Profile Private -Protocol TCP `
    -LocalPort $Port -RemoteAddress LocalSubnet | Out-Null
Write-Host "Allowed TCP $Port on PRIVATE networks, from the local subnet only." -ForegroundColor Green
Write-Host "To remove it later (as Administrator): Remove-NetFirewallRule -DisplayName '$name'"
