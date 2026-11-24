[CmdletBinding()]
param([string]$ServiceName = "ListenTogetherRelay")
$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell window (Run as Administrator)."
}
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName
    if ($LASTEXITCODE -ne 0) { throw "Windows Service removal failed with exit code $LASTEXITCODE." }
    Write-Host "Removed service '$ServiceName'. Installed files were retained."
} else {
    Write-Host "Service '$ServiceName' is not installed."
}
