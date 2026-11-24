[CmdletBinding()]
param([string]$RulePrefix = "ListenTogether Home Server")

$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    throw "Run this script from PowerShell opened with 'Run as administrator'. No router settings have been changed."
}

$firewallScript = Join-Path $PSScriptRoot "Configure-Firewall.ps1"
if (-not (Test-Path $firewallScript)) {
    throw "Missing firewall configuration script: $firewallScript"
}

Write-Host "Configuring Windows Firewall..." -ForegroundColor Cyan
& $firewallScript -RulePrefix $RulePrefix
if (-not $?) {
    throw "Windows Firewall configuration failed. Review the error above; router settings have not been changed."
}

$lanAdapters = @(Get-NetIPAddress -AddressFamily IPv4 -AddressState Preferred |
    Where-Object {
        $_.IPAddress -notlike "127.*" -and
        $_.IPAddress -notlike "169.254.*" -and
        $_.PrefixOrigin -ne "WellKnown"
    } |
    ForEach-Object {
        $adapter = Get-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue
        [pscustomobject]@{
            Address = $_.IPAddress
            Interface = if ($adapter) { $adapter.Name } else { "Interface $($_.InterfaceIndex)" }
            Status = if ($adapter) { $adapter.Status } else { "Unknown" }
        }
    } |
    Sort-Object Status, Interface -Descending -Unique)

Write-Host ""
Write-Host "Windows Firewall setup is complete." -ForegroundColor Green
Write-Host "Choose the IPv4 address belonging to the Ethernet/Wi-Fi adapter connected to your router (not a VPN adapter):"
foreach ($adapter in $lanAdapters) {
    Write-Host ("  {0,-18} {1} [{2}]" -f $adapter.Address, $adapter.Interface, $adapter.Status) -ForegroundColor Yellow
}

Write-Host ""
Write-Host "NOW CHANGE THESE SETTINGS IN YOUR ROUTER" -ForegroundColor Cyan
Write-Host "1. Open the router administration page."
Write-Host "2. Find Port Forwarding, Virtual Server, NAT Rules, or a similarly named page."
Write-Host "3. Create THREE rules using the table below. If a rule already exists for a port, edit it to match."
@"
Rule name                    Protocol   External/WAN port   Internal/LAN IP       Internal/LAN port   Enabled
ListenTogether HTTP          TCP        80                  <HOST_LAN_IP>         80                  Yes
ListenTogether HTTPS         TCP        443                 <HOST_LAN_IP>         443                 Yes
ListenTogether STUN          UDP        3478                <HOST_LAN_IP>         3478                Yes
"@ | Write-Host
Write-Host "Replace <HOST_LAN_IP> with the Ethernet/Wi-Fi address printed above."
Write-Host "If the router asks for Source IP / Remote host, leave it blank or choose Any."
Write-Host "If the router asks for a schedule, choose Always. Use the exact protocol shown."
Write-Host "4. Save/apply the rules. Also create a DHCP reservation for the same host LAN IP so it cannot change."
Write-Host ""
Write-Host "Do NOT create a port-forward for TCP 5000. It must remain private on the host PC."
Write-Host "Do NOT use the public IP as the internal/LAN destination; use the host PC's local IPv4 address."
Write-Host ""
Write-Host "After starting ListenTogether and Caddy, run Test-HomeServer.ps1 from a different internet connection."
Write-Host "For exact target-IP details in its output, pass the LAN address too:"
Write-Host '  .\scripts\home-server\Test-HomeServer.ps1 -Server "YOUR_PUBLIC_IP" -HostLanIp "HOST_LAN_IP"'
Write-Host "The diagnostic prints the exact rule to add or correct for every failed port."
Write-Host "A test from your own Wi-Fi may be misleading if NAT loopback is unsupported."
