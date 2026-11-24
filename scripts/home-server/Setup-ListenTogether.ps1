[CmdletBinding()]
param(
    [string]$RulePrefix = "ListenTogether Home Server"
)

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
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
    throw "Windows Firewall configuration failed."
}

$lanAddresses = @(Get-NetIPAddress -AddressFamily IPv4 -AddressState Preferred |
    Where-Object {
        $_.IPAddress -notlike "127.*" -and
        $_.IPAddress -notlike "169.254.*" -and
        $_.PrefixOrigin -ne "WellKnown"
    } |
    Select-Object -ExpandProperty IPAddress -Unique)

Write-Host ""
Write-Host "Windows setup is complete." -ForegroundColor Green
Write-Host "Reserve this PC's LAN address in your router's DHCP reservation settings:"
foreach ($address in $lanAddresses) {
    Write-Host "  $address" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Create these PORT FORWARDING rules in your router:" -ForegroundColor Cyan
@"
Protocol   External port   Internal IP        Internal port
TCP        80              <this PC LAN IP>   80
TCP        443             <this PC LAN IP>   443
UDP        3478            <this PC LAN IP>   3478
"@ | Write-Host
Write-Host "Do not forward TCP 5000. It must remain local to this PC."
Write-Host ""
Write-Host "Router administration pages differ by manufacturer, so this script does not guess"
Write-Host "at your router's controls. After saving the rules, start the server and run"
Write-Host "Test-HomeServer.ps1 from a device on a DIFFERENT network (for example, mobile hotspot)."
Write-Host "That diagnostic identifies which public ports are unreachable and the exact forwarding"
Write-Host "rule to check. A test from your own Wi-Fi may be misleading if NAT loopback is unsupported."
