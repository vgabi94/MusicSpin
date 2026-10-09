<#
.SYNOPSIS
  Installs the FFmpeg shared DLLs MusicSpin loads at runtime (FFmpeg 9.0 LGPL, matching FFmpeg.AutoGen 9.0.x)
  from https://github.com/BtbN/FFmpeg-Builds into %LOCALAPPDATA%\MusicSpin\ffmpeg (found automatically).
.PARAMETER Destination
  Optional target directory. If you use a custom one, set MUSICSPIN_FFMPEG_PATH to its bin folder.
#>
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'MusicSpin\ffmpeg')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$series = '9.0'
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'winarm64' } else { 'win64' }
$name = "ffmpeg-n$series-latest-$arch-lgpl-shared-$series"
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$name.zip"

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile (Join-Path $tmp 'ffmpeg.zip')
    Expand-Archive -Path (Join-Path $tmp 'ffmpeg.zip') -DestinationPath $tmp
    if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
    New-Item -ItemType Directory -Force -Path (Split-Path $Destination) | Out-Null
    Move-Item (Join-Path $tmp $name) $Destination
    Write-Host "FFmpeg $series (LGPL, shared) installed to $Destination"
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
