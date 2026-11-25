$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet restore ListenTogether.sln
    dotnet test ListenTogether.sln --configuration Release
    dotnet publish src/ListenTogether.App/ListenTogether.App.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output artifacts/publish

    New-Item -ItemType Directory -Force artifacts/release | Out-Null
    Compress-Archive -Path artifacts/publish/* -DestinationPath artifacts/release/ListenTogether-win-x64.zip -Force

    Write-Host "Created artifacts/release/ListenTogether-win-x64.zip"
}
finally {
    Pop-Location
}
