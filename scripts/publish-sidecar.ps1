#Requires -Version 5.1
# Publishes the server as one self-contained win-x64 exe where Tauri's externalBin expects it.
# No trimming, no Native AOT and no single-file compression: EF Core and SignalR break or start slower with them.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Ofnr.Server'
$out = Join-Path $project 'bin\publish-win-x64'

& dotnet publish $project -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$bin = Join-Path $root 'desktop\src-tauri\binaries'
New-Item -ItemType Directory -Force $bin | Out-Null
Copy-Item (Join-Path $out 'Ofnr.Server.exe') (Join-Path $bin 'ofnr-server-x86_64-pc-windows-msvc.exe') -Force
Write-Host "sidecar: $(Join-Path $bin 'ofnr-server-x86_64-pc-windows-msvc.exe')"
