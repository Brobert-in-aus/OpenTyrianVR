# Downloads the pinned SDL2 development libraries for the MSVC build and
# generates the .props files the solution expects. Safe to re-run; skips
# downloads that are already extracted.
#
# Usage: powershell -ExecutionPolicy Bypass -File visualc\fetch-deps.ps1

$ErrorActionPreference = 'Stop'

$Sdl2Version = '2.32.10'
$Sdl2NetVersion = '2.2.0'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$DepsDir = Join-Path $RepoRoot 'deps'
$Sdl2Dir = Join-Path $DepsDir "SDL2-$Sdl2Version"
$Sdl2NetDir = Join-Path $DepsDir "SDL2_net-$Sdl2NetVersion"

$Downloads = @(
    @{
        Url = "https://github.com/libsdl-org/SDL/releases/download/release-$Sdl2Version/SDL2-devel-$Sdl2Version-VC.zip"
        Target = $Sdl2Dir
    },
    @{
        Url = "https://github.com/libsdl-org/SDL_net/releases/download/release-$Sdl2NetVersion/SDL2_net-devel-$Sdl2NetVersion-VC.zip"
        Target = $Sdl2NetDir
    }
)

New-Item -ItemType Directory -Force $DepsDir | Out-Null

foreach ($dl in $Downloads) {
    if (Test-Path (Join-Path $dl.Target 'include')) {
        Write-Host "Already present: $($dl.Target)"
        continue
    }
    $zip = Join-Path $DepsDir ([System.IO.Path]::GetFileName($dl.Url))
    Write-Host "Downloading $($dl.Url)"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $dl.Url -OutFile $zip -UseBasicParsing
    # The zips contain a single SDL2-x.y.z / SDL2_net-x.y.z root folder.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $DepsDir)
    Remove-Item $zip
    if (-not (Test-Path (Join-Path $dl.Target 'include'))) {
        throw "Extraction did not produce expected directory: $($dl.Target)"
    }
}

$SdlPathsProps = @"
<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <SDL2BaseDir>$Sdl2Dir</SDL2BaseDir>
    <SDL2netBaseDir>$Sdl2NetDir</SDL2netBaseDir>
  </PropertyGroup>
</Project>
"@
Set-Content -Path (Join-Path $PSScriptRoot 'sdl_paths.props') -Value $SdlPathsProps -Encoding utf8

if (-not (Test-Path (Join-Path $PSScriptRoot 'opentyrian.props'))) {
    Copy-Item (Join-Path $PSScriptRoot 'opentyrian.props.template') (Join-Path $PSScriptRoot 'opentyrian.props')
}

Write-Host "Dependencies ready. SDL2 $Sdl2Version, SDL2_net $Sdl2NetVersion in $DepsDir"
