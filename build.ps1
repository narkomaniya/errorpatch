$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$game = 'C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley'
$env:SMAPI_DIR = $game

Set-Location $root
dotnet build -c Release

$source = Join-Path $root 'bin\Release\net6.0\ErrorPatch.dll'
$targetDir = Join-Path $game 'Mods\ErrorPatch'
New-Item -ItemType Directory -Force $targetDir | Out-Null
Copy-Item $source (Join-Path $targetDir 'ErrorPatch.dll') -Force
Copy-Item (Join-Path $root 'manifest.json') (Join-Path $targetDir 'manifest.json') -Force

$downloads = Join-Path $game 'Mods\downloads.json'
if (-not (Test-Path $downloads)) {
    Copy-Item (Join-Path $root 'downloads.json.example') $downloads
}

Write-Host "Installed SMAPI mod to $targetDir"
