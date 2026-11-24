[CmdletBinding()]
param(
    [switch]$Once,
    [int]$IntervalSeconds = 300
)

$ErrorActionPreference = "Stop"
$directory = Join-Path $env:LOCALAPPDATA "ListenTogether"
$tokenPath = Join-Path $directory "duckdns-token.txt"
$domainPath = Join-Path $directory "duckdns-domain.txt"
if (-not (Test-Path $tokenPath) -or -not (Test-Path $domainPath)) {
    throw "DuckDNS is not configured. Run Configure-DuckDns.ps1 first."
}

$domain = (Get-Content $domainPath -Raw).Trim()
$secureToken = Get-Content $tokenPath -Raw | ConvertTo-SecureString
$tokenPtr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
try {
    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenPtr)
    do {
        $uri = "https://www.duckdns.org/update?domains=$([uri]::EscapeDataString($domain))&token=$([uri]::EscapeDataString($token))&verbose=true"
        $response = (Invoke-WebRequest -Uri $uri -Method Get -TimeoutSec 20).Content.Trim()
        if ($response -notmatch '(?m)^OK(?:\s|$)') {
            throw "DuckDNS did not confirm the update. Response: $response"
        }
        Write-Host "$(Get-Date -Format o) DuckDNS updated successfully for $domain.duckdns.org." -ForegroundColor Green
        if ($Once) { break }
        Start-Sleep -Seconds ([Math]::Max(60, $IntervalSeconds))
    } while ($true)
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPtr)
    $token = $null
}
