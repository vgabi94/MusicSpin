<#
.SYNOPSIS
  Portable, self-contained single-file builds of the MusicSpin desktop app and CLI (Windows-friendly twin of publish.sh).
.EXAMPLE
  ./eng/publish.ps1                         # this machine's RID, app + CLI
  ./eng/publish.ps1 -Rid win-x64,win-arm64 -DownloadFFmpeg -Zip
  ./eng/publish.ps1 -All -CliOnly
.NOTES
  Layout of <Out>/<rid>/: music-spin(.exe), MusicSpin(.exe), TagLibSharp.dll (LGPL, kept replaceable),
  ffmpeg/ (LGPL FFmpeg shared libraries, optional), LICENSE, THIRD-PARTY-NOTICES.md; on Linux also MusicSpin.png (the
  app icon, for a .desktop launcher). Data goes to .usrdata/ on first run.
  macOS .app bundles are produced by publish.sh on macOS (needs codesign).
#>
[CmdletBinding()]
param(
    [string[]] $Rid,
    [switch] $All,
    [switch] $AppOnly,
    [switch] $CliOnly,
    [string] $FFmpegDir,
    [switch] $DownloadFFmpeg,
    [string] $Out,
    [string] $Configuration = 'Release',
    [switch] $Zip
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root 'artifacts/publish' }
$allRids = 'win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'
if ($All) { $Rid = $allRids }
if (-not $Rid) {
    $os = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
    $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    $Rid = @("$os-$arch")
}
$projects = @()
if (-not $AppOnly) { $projects += Join-Path $root 'src/MusicSpin.Cli/MusicSpin.Cli.csproj' }
if (-not $CliOnly) { $projects += Join-Path $root 'src/MusicSpin.App/MusicSpin.App.csproj' }
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

function Copy-FFmpeg([string] $Source, [string] $Dest, [string] $TargetRid) {
    New-Item -ItemType Directory -Force -Path $Dest | Out-Null
    if ($TargetRid -like 'win-*') {
        $bin = if (Test-Path (Join-Path $Source 'bin')) { Join-Path $Source 'bin' } else { $Source }
        Get-ChildItem $bin -File | Where-Object { $_.Name -match '^(avcodec|avformat|avutil|swresample)-\d+\.dll$' } | Copy-Item -Destination $Dest
    } else {
        $lib = if (Test-Path (Join-Path $Source 'lib')) { Join-Path $Source 'lib' } else { $Source }
        Get-ChildItem $lib | Where-Object { $_.Name -match '^lib(avcodec|avformat|avutil|swresample)\.(so\.\d+|\d+\.dylib)$' } |
            ForEach-Object { Copy-Item -LiteralPath (Resolve-Path $_.FullName) -Destination (Join-Path $Dest $_.Name) }
    }
    foreach ($f in 'LICENSE.txt', 'LICENSE', 'COPYING.LGPLv2.1', 'COPYING.LGPLv3') {
        if (Test-Path (Join-Path $Source $f)) { Copy-Item (Join-Path $Source $f) $Dest }
    }
    'FFmpeg shared libraries (LGPL). Replace with any FFmpeg 9.0 shared build. Source: https://ffmpeg.org' |
        Set-Content (Join-Path $Dest 'README.txt')
}

function Get-FFmpeg([string] $TargetRid, [string] $Dest) {
    $map = @{ 'win-x64' = 'win64'; 'win-arm64' = 'winarm64'; 'linux-x64' = 'linux64'; 'linux-arm64' = 'linuxarm64' }
    if (-not $map.ContainsKey($TargetRid)) { Write-Host "  (no prebuilt LGPL FFmpeg for $TargetRid)"; return }
    $name = "ffmpeg-n9.0-latest-$($map[$TargetRid])-lgpl-shared-9.0"
    $ext = if ($TargetRid -like 'win-*') { 'zip' } else { 'tar.xz' }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid())
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $archive = Join-Path $tmp "ffmpeg.$ext"
        Invoke-WebRequest "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$name.$ext" -OutFile $archive
        if ($ext -eq 'zip') { Expand-Archive $archive -DestinationPath $tmp } else { tar -xJf $archive -C $tmp }
        Copy-FFmpeg (Join-Path $tmp $name) $Dest $TargetRid
    } finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
}

foreach ($r in $Rid) {
    $dest = Join-Path $Out $r
    Write-Host "== $r -> $dest"
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    foreach ($p in $projects) {
        dotnet publish $p -c $Configuration -r $r -o $dest -nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $p ($r)" }
    }
    if ($r -like 'linux-*' -and (Test-Path (Join-Path $dest 'MusicSpin'))) {
        Copy-Item (Join-Path $root 'src/MusicSpin.App/Assets/AppIcon/app-icon-512.png') (Join-Path $dest 'MusicSpin.png')
    }
    if ($FFmpegDir) { Copy-FFmpeg $FFmpegDir (Join-Path $dest 'ffmpeg') $r }
    elseif ($DownloadFFmpeg) { Get-FFmpeg $r (Join-Path $dest 'ffmpeg') }
    Copy-Item (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD-PARTY-NOTICES.md') $dest
    Get-ChildItem $dest -Filter *.pdb | Remove-Item
    Get-ChildItem $dest | Format-Table Name, Length -AutoSize | Out-String | Write-Host
    if ($Zip) {
        $zipPath = Join-Path $Out "musicspin-$version-$r.zip"
        if (Test-Path $zipPath) { Remove-Item $zipPath }
        Compress-Archive -Path (Join-Path $dest '*') -DestinationPath $zipPath
        Write-Host "  -> $zipPath"
    }
}
