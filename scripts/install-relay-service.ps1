[CmdletBinding()]
param(
    [string]$InstallDirectory = "$env:ProgramFiles\ListenTogether\Relay",
    [string]$ServiceName = "ListenTogetherRelay"
)
$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell window (Run as Administrator)."
}
$source = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\artifacts\relay-server"))
$destination = [System.IO.Path]::GetFullPath($InstallDirectory)
$executable = Join-Path $destination "ListenTogether.Server.exe"
if (-not (Test-Path (Join-Path $source "ListenTogether.Server.exe"))) {
    throw "Published server not found. Run scripts/publish-relay-server.ps1 first."
}
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item (Join-Path $source "*") $destination -Recurse -Force
$binaryPath = 'binPath= "' + $executable + '"'
& sc.exe create $ServiceName $binaryPath "start= auto" "DisplayName= ListenTogether Relay"
if ($LASTEXITCODE -ne 0) { throw "Windows Service creation failed with exit code $LASTEXITCODE." }
& sc.exe description $ServiceName "Hosts ListenTogether audio sessions on this Windows PC."
if ($LASTEXITCODE -ne 0) { throw "Could not set the Windows Service description." }
Write-Host "Installed ListenTogether Relay as an automatic Windows Service."
Write-Host "It has deliberately not been started. Configure HTTPS before exposing it to the internet."
Write-Host "Install directory: $destination"
