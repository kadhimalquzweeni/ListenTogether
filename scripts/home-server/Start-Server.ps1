[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Domain,
    [Parameter(Mandatory = $true)][string]$AcmeEmail,
    [string]$ServerExe = (Join-Path $PSScriptRoot "..\..\artifacts\server\ListenTogether.Server.exe"),
    [string]$CaddyExe = "caddy"
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path $ServerExe)) { throw "Server executable not found: $ServerExe. Publish ListenTogether.Server for win-x64 first." }
$caddyCommand = Get-Command $CaddyExe -ErrorAction SilentlyContinue
if (-not $caddyCommand -and -not (Test-Path $CaddyExe)) { throw "Caddy was not found. Install Caddy and put caddy.exe on PATH, or pass -CaddyExe." }
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$caddyfile = Join-Path $repoRoot "deploy\Caddyfile.windows"
if (-not (Test-Path $caddyfile)) { throw "Caddy configuration not found: $caddyfile" }
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:ACME_EMAIL = $AcmeEmail
$env:LISTENTOGETHER_DOMAIN = $Domain
# Kestrel stays on loopback HTTP; Caddy terminates public HTTPS. STUN binds 0.0.0.0:3478/UDP.
$server = Start-Process -FilePath $ServerExe -WorkingDirectory (Split-Path $ServerExe) -PassThru
Write-Host "Started ListenTogether server (PID $($server.Id))."
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            Invoke-RestMethod -Uri "http://127.0.0.1:5000/healthz" -TimeoutSec 2 | Out-Null
            $ready = $true
            break
        } catch { Start-Sleep -Seconds 1 }
    }
    if (-not $ready) { throw "Server did not become healthy at http://127.0.0.1:5000/healthz." }
    $caddyArgs = @("run", "--config", $caddyfile, "--adapter", "caddyfile")
    if ($caddyCommand) {
        $caddy = Start-Process -FilePath $caddyCommand.Source -ArgumentList $caddyArgs -WorkingDirectory $repoRoot -PassThru
    } else {
        $caddy = Start-Process -FilePath $CaddyExe -ArgumentList $args -WorkingDirectory $repoRoot -PassThru
    }
    Write-Host "Started Caddy (PID $($caddy.Id))." -ForegroundColor Green
    Write-Host "Public endpoint: https://$Domain"
    Write-Host "STUN endpoint: $($Domain):3478/UDP"
    Write-Host "Keep this window open. Caddy needs public DNS and inbound TCP 80/443 to obtain its TLS certificate."
    Write-Host "Stop the processes with: Stop-Process -Id $($caddy.Id),$($server.Id)"
} catch {
    Stop-Process -Id $server.Id -ErrorAction SilentlyContinue
    throw
}
