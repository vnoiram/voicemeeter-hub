# Publishes the hub and builds the per-user Inno Setup installer. Run on Windows with Inno Setup
# (iscc) installed and on PATH (e.g. `choco install innosetup`).
[CmdletBinding()]
param(
    [string]$Version = "0.1.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishDir = Join-Path $root "dist/hub"
$iss = Join-Path $root "installer/voicemeeter-hub.iss"

# 1. Publish the self-contained single-file win-x64 executable.
& (Join-Path $PSScriptRoot "publish.ps1") -Configuration $Configuration -OutDir "dist/hub"

# 2. Compile the installer.
$iscc = Get-Command iscc -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidate = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    if (Test-Path $candidate) { $iscc = $candidate } else { throw "iscc (Inno Setup) not found. Install it: choco install innosetup" }
} else {
    $iscc = $iscc.Source
}

& $iscc "/DAppVersion=$Version" "/DSourceDir=$publishDir" $iss
Write-Host "Installer written to $(Join-Path $root 'installer/Output')"
