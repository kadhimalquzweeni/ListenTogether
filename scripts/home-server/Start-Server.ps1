[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AcmeEmail,
    [string]$StatusFile,
    [switch]$HideChildWindows,
    [string]$ServerExe,
    [string]$CaddyExe = "caddy"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

# Resolve script-relative defaults after the script has entered its execution scope.
# Automatic variables such as $PSScriptRoot may be empty in parameter default expressions.
if ([string]::IsNullOrWhiteSpace($ServerExe)) {
    $ServerExe = Join-Path $PSScriptRoot "..\..\artifacts\server\ListenTogether.Server.exe"
}

function Test-PublicIPv4 {
    param([System.Net.IPAddress]$Address)
    if ($Address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { return $false }
    if ([System.Net.IPAddress]::IsLoopback($Address)) { return $false }
    $b = $Address.GetAddressBytes()
    if ($b[0] -eq 0 -or $b[0] -eq 10 -or $b[0] -eq 127 -or $b[0] -ge 224) { return $false }
    if ($b[0] -eq 100 -and $b[1] -ge 64 -and $b[1] -le 127) { return $false }
    if ($b[0] -eq 169 -and $b[1] -eq 254) { return $false }
    if ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) { return $false }
    if ($b[0] -eq 192 -and $b[1] -eq 168) { return $false }
    if ($b[0] -eq 192 -and $b[1] -eq 0 -and $b[2] -eq 2) { return $false }
    if ($b[0] -eq 198 -and ($b[1] -eq 18 -or $b[1] -eq 19 -or $b[1] -eq 51)) { return $false }
    if ($b[0] -eq 203 -and $b[1] -eq 0 -and $b[2] -eq 113) { return $false }
    return $true
}

function Get-PublicIPv4 {
    $providers = @("https://api.ipify.org", "https://checkip.amazonaws.com")
    $detected = [System.Collections.Generic.List[string]]::new()
    foreach ($provider in $providers) {
        try {
            $value = (Invoke-RestMethod -Uri $provider -TimeoutSec 10).ToString().Trim()
            $address = $null
            if (-not [System.Net.IPAddress]::TryParse($value, [ref]$address) -or
                -not (Test-PublicIPv4 -Address $address)) {
                throw "The provider returned an invalid or non-public IPv4 address."
            }
            $detected.Add($address.ToString())
        }
        catch {
            Write-Warning "A public IP discovery service could not be reached or returned an invalid address."
        }
    }
    if ($detected.Count -eq 0) {
        throw "Couldn't detect a public IPv4 address. Check the internet connection and try again."
    }
    if (@($detected | Select-Object -Unique).Count -gt 1) {
        throw "Public IP discovery services returned different addresses. Refusing to start with an uncertain address."
    }
    return $detected[0]
}

if (-not (Test-Path $ServerExe -PathType Leaf)) {
    $serverProject = Join-Path $repoRoot "src\ListenTogether.Server\ListenTogether.Server.csproj"
    if (-not (Test-Path $serverProject -PathType Leaf)) {
        throw "The server executable is missing and the server project could not be found: $serverProject"
    }

    $dotnet = Get-Command "dotnet" -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw "The ListenTogether server has not been published yet and the .NET 10 SDK was not found. Install the .NET 10 SDK, restart ListenTogether, and click Start Server again."
    }

    Write-Host "The ListenTogether server has not been published yet. Publishing it now..." -ForegroundColor Cyan
    Write-Host "Project: $serverProject"
    Write-Host "Output:  $(Split-Path -Parent $ServerExe)"
    & $dotnet.Source publish $serverProject --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output (Split-Path -Parent $ServerExe)
    if ($LASTEXITCODE -ne 0) {
        throw "Automatic server publishing failed (dotnet publish exit code $LASTEXITCODE). Fix the build errors above and click Start Server to retry."
    }

    if (-not (Test-Path $ServerExe -PathType Leaf)) {
        throw "dotnet publish completed, but the server executable was not created at the expected path: $ServerExe"
    }

    Write-Host "Server published successfully. Continuing with server startup..." -ForegroundColor Green
}
function Resolve-CaddyExecutable {
    param([string]$RequestedPath)

    # A desktop app may have been started before winget updated the user's PATH.
    # Refresh PATH from the registry-backed user and machine values for this process.
    $pathEntries = @(
        $env:Path,
        [Environment]::GetEnvironmentVariable("Path", "User"),
        [Environment]::GetEnvironmentVariable("Path", "Machine")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $env:Path = ($pathEntries -join [IO.Path]::PathSeparator)

    $command = Get-Command $RequestedPath -ErrorAction SilentlyContinue
    if ($command -and $command.Source -and (Test-Path $command.Source)) {
        return $command.Source
    }

    if (Test-Path $RequestedPath) {
        return (Resolve-Path $RequestedPath).Path
    }

    # winget commonly exposes installed command-line tools through this stable shim.
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates += Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\caddy.exe"
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidates += Join-Path $env:ProgramFiles "Caddy\caddy.exe"
        $candidates += Join-Path $env:ProgramFiles "CaddyServer\Caddy\caddy.exe"
    }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate -PathType Leaf) {
            return (Resolve-Path $candidate).Path
        }
    }

    return $null
}

$caddyPath = Resolve-CaddyExecutable -RequestedPath $CaddyExe
if (-not $caddyPath) {
    throw "Caddy was not found. Install Caddy 2.10 or later with winget (winget install CaddyServer.Caddy), restart ListenTogether so it receives the updated PATH, or pass -CaddyExe with the full path to caddy.exe."
}
$versionOutput = (& $caddyPath version 2>&1 | Select-Object -First 1).ToString()
if ($versionOutput -notmatch 'v?(\d+)\.(\d+)') {
    throw "Couldn't determine the Caddy version: $versionOutput"
}
$caddyMajor = [int]$Matches[1]
$caddyMinor = [int]$Matches[2]
if ($caddyMajor -lt 2 -or ($caddyMajor -eq 2 -and $caddyMinor -lt 10)) {
    throw "Caddy 2.10 or later is required for ACME certificate profile support. Found: $versionOutput"
}

$publicIp = Get-PublicIPv4
$publicUrl = "https://$publicIp"
$caddyfile = Join-Path $repoRoot "deploy\Caddyfile.windows"
if (-not (Test-Path $caddyfile)) { throw "Caddy configuration not found: $caddyfile" }

$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:ACME_EMAIL = $AcmeEmail
$env:LISTENTOGETHER_PUBLIC_IP = $publicIp

& $caddyPath validate --config $caddyfile --adapter caddyfile
if ($LASTEXITCODE -ne 0) {
    throw "Caddy rejected its configuration. Check the Caddyfile and detected IP address."
}

$settingsDirectory = Join-Path $env:LOCALAPPDATA "ListenTogether"
New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
$addressPath = Join-Path $settingsDirectory "server-address.txt"
$processManifestPath = Join-Path $settingsDirectory "server-processes.json"
Set-Content -Path $addressPath -Value $publicUrl -NoNewline -Encoding ASCII

# Kestrel stays on loopback HTTP; Caddy terminates public HTTPS. STUN binds 0.0.0.0:3478/UDP.
$childWindowStyle = if ($HideChildWindows) { "Hidden" } else { "Normal" }
$server = Start-Process -FilePath $ServerExe -WorkingDirectory (Split-Path $ServerExe) -WindowStyle $childWindowStyle -PassThru
Write-Host "Started ListenTogether server (PID $($server.Id))."
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            Invoke-RestMethod -Uri "http://127.0.0.1:5000/healthz" -TimeoutSec 2 | Out-Null
            $ready = $true
            break
        }
        catch { Start-Sleep -Seconds 1 }
    }
    if (-not $ready) { throw "Server did not become healthy at http://127.0.0.1:5000/healthz." }

    $caddyArgs = @("run", "--config", $caddyfile, "--adapter", "caddyfile")
    $caddy = Start-Process -FilePath $caddyPath -ArgumentList $caddyArgs -WorkingDirectory $repoRoot -WindowStyle $childWindowStyle -PassThru

    # Record exact process identities so shutdown never targets unrelated processes.
    $serverProcess = Get-Process -Id $server.Id
    $caddyProcess = Get-Process -Id $caddy.Id
    $launcherProcess = Get-Process -Id $PID
    @{
        Processes = @(
            @{ Id = $server.Id; Path = $serverProcess.Path; StartTimeUtcTicks = $serverProcess.StartTime.ToUniversalTime().Ticks },
            @{ Id = $caddy.Id; Path = $caddyProcess.Path; StartTimeUtcTicks = $caddyProcess.StartTime.ToUniversalTime().Ticks },
            @{ Id = $PID; Path = $launcherProcess.Path; StartTimeUtcTicks = $launcherProcess.StartTime.ToUniversalTime().Ticks }
        )
    } | ConvertTo-Json -Depth 4 | Set-Content -Path $processManifestPath -Encoding UTF8

    if (-not [string]::IsNullOrWhiteSpace($StatusFile)) {
        $statusDirectory = Split-Path -Parent $StatusFile
        New-Item -ItemType Directory -Path $statusDirectory -Force | Out-Null
        Set-Content -Path $StatusFile -Value $publicUrl -NoNewline -Encoding ASCII
    }

    Write-Host ""
    Write-Host "ListenTogether is starting for your current public IP." -ForegroundColor Green
    Write-Host "Server address: $publicUrl"
    Write-Host "STUN endpoint: ${publicIp}:3478/UDP"
    Write-Host "Saved server address to: $addressPath"
    Write-Host "Give your friend the server address and the session invite code shown by the desktop app."
    Write-Host ""
    Write-Host "Caddy obtains and renews a short-lived Let's Encrypt IP certificate automatically."
    if ($HideChildWindows) {
        Write-Host "Server and Caddy console windows were started hidden."
    }
    else {
        Write-Host "Keep this window open. Your router must forward TCP 80/443 and UDP 3478 to this PC."
    }
    Write-Host "Stop the processes with: Stop-Process -Id $($caddy.Id),$($server.Id)"
}
catch {
    if ($caddy) { Stop-Process -Id $caddy.Id -ErrorAction SilentlyContinue }
    Stop-Process -Id $server.Id -ErrorAction SilentlyContinue
    if (Test-Path $processManifestPath) { Remove-Item $processManifestPath -Force }
    throw
}
