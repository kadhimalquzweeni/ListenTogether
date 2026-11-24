[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\artifacts\relay-server")
)
$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\ListenTogether.Server\ListenTogether.Server.csproj"
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $output) { Remove-Item $output -Recurse -Force }
New-Item -ItemType Directory -Path $output -Force | Out-Null
dotnet publish $project --configuration Release --runtime $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output $output
if ($LASTEXITCODE -ne 0) { throw "Relay server publish failed with exit code $LASTEXITCODE." }
Write-Host "Published ListenTogether relay server to: $output"
Write-Host "The server is self-contained and does not require a separately installed .NET runtime."
