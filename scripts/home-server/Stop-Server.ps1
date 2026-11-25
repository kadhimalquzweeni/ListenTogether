[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$settingsDirectory = Join-Path $env:LOCALAPPDATA "ListenTogether"
$manifestPath = Join-Path $settingsDirectory "server-processes.json"

$processEntries = @()
if (Test-Path $manifestPath -PathType Leaf) {
    $manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
    $processEntries = @($manifest.Processes)
}
else {
    # Backward compatibility: discover only the exact ListenTogether server,
    # Caddy using this repository's Caddyfile, and its startup PowerShell window.
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\\..")).Path
    $serverExe = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\\server\\ListenTogether.Server.exe"))
    $caddyfile = [IO.Path]::GetFullPath((Join-Path $repoRoot "deploy\\Caddyfile.windows"))
    $startScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "Start-Server.ps1"))

    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and [string]::Equals([IO.Path]::GetFullPath($_.Path), $serverExe, [StringComparison]::OrdinalIgnoreCase)
    })) {
        $processEntries += @{ Id = $process.Id; Path = $process.Path; StartTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks }
    }

    foreach ($nativeProcess in @(Get-CimInstance Win32_Process -Filter "Name = 'caddy.exe'" -ErrorAction SilentlyContinue)) {
        if ($nativeProcess.CommandLine -and $nativeProcess.CommandLine.IndexOf($caddyfile, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $process = Get-Process -Id ([int]$nativeProcess.ProcessId) -ErrorAction SilentlyContinue
            if ($process) {
                $processEntries += @{ Id = $process.Id; Path = $process.Path; StartTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks }
            }
        }
    }

    foreach ($nativeProcess in @(Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction SilentlyContinue)) {
        if ($nativeProcess.CommandLine -and $nativeProcess.CommandLine.IndexOf($startScript, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $process = Get-Process -Id ([int]$nativeProcess.ProcessId) -ErrorAction SilentlyContinue
            if ($process) {
                $processEntries += @{ Id = $process.Id; Path = $process.Path; StartTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks }
            }
        }
    }
}

if ($processEntries.Count -eq 0) {
    Write-Host "No ListenTogether server processes are currently running."
    exit 0
}

$failures = @()
foreach ($entry in @($processEntries | Sort-Object {
    if ($_.Path -match 'caddy') { 0 }
    elseif ($_.Path -match 'ListenTogether.Server') { 1 }
    else { 2 }
})) {
    $processId = [int]$entry.Id
    try {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if (-not $process) {
            Write-Host "Process $processId is already stopped."
            continue
        }

        $actualPath = $process.Path
        $actualStartTicks = $process.StartTime.ToUniversalTime().Ticks
        $expectedPath = [IO.Path]::GetFullPath([string]$entry.Path)
        if ([string]::IsNullOrWhiteSpace($actualPath) -or
            -not [string]::Equals([IO.Path]::GetFullPath($actualPath), $expectedPath, [StringComparison]::OrdinalIgnoreCase) -or
            $actualStartTicks -ne [long]$entry.StartTimeUtcTicks) {
            Write-Warning "Skipping PID $processId because it no longer matches the recorded ListenTogether process identity."
            continue
        }

        Stop-Process -Id $processId -Force -ErrorAction Stop
        Write-Host "Stopped $([IO.Path]::GetFileName($actualPath)) (PID $processId)."
    }
    catch {
        $failures += ("PID {0}: {1}" -f $processId, $_.Exception.Message)
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "One or more ListenTogether processes could not be stopped. The process manifest has been retained for retry."
}

if (Test-Path $manifestPath) { Remove-Item -Path $manifestPath -Force }
Write-Host "ListenTogether server, STUN and Caddy have been stopped." -ForegroundColor Green
