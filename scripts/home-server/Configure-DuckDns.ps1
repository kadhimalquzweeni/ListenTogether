[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-zA-Z0-9-]{1,63}$')]
    [string]$Subdomain
)

$ErrorActionPreference = "Stop"
$token = Read-Host "Paste your DuckDNS token" -AsSecureString
if ($token.Length -lt 20) { throw "The token looks too short. Copy the token from your DuckDNS account page." }

$directory = Join-Path $env:LOCALAPPDATA "ListenTogether"
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$tokenPath = Join-Path $directory "duckdns-token.txt"
$domainPath = Join-Path $directory "duckdns-domain.txt"

# SecureString is encrypted with Windows DPAPI for the current Windows user on this PC.
$token | ConvertFrom-SecureString | Set-Content -Path $tokenPath -NoNewline
$Subdomain | Set-Content -Path $domainPath -NoNewline
Write-Host "Saved DuckDNS configuration for $Subdomain.duckdns.org." -ForegroundColor Green
Write-Host "The token is encrypted for this Windows account and is not committed to the repository."
Write-Host "Run scripts\home-server\Update-DuckDns.ps1 to keep the DNS record current."
