# Publishes the hub as a self-contained single-file win-x64 executable to dist/hub.
# Run on Windows (or via a Windows .NET SDK container). Voicemeeter itself is not required to build.
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutDir = "dist/hub"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "src/VoicemeeterHub/VoicemeeterHub.csproj"
$output = Join-Path $root $OutDir

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    -f net8.0-windows `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableWindowsTargeting=true `
    -o $output

Write-Host "Published VoicemeeterHub.exe to $output"
