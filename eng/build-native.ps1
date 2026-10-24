# Builds MusicSpin's native audio layer (native/msaudio: miniaudio plus a flat C API) for a Windows RID with MSVC.
#
# Usage: eng/build-native.ps1 -Rid win-x64|win-arm64 [-Out DIR]   (default DIR: native/msaudio/bin/<rid>)
# Needs Visual Studio or the Build Tools with the C++ workload (and the ARM64 tools for win-arm64); found via vswhere.
param(
    [Parameter(Mandatory)] [ValidateSet('win-x64', 'win-arm64')] [string] $Rid,
    [string] $Out
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root "native/msaudio/bin/$Rid" }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found: install Visual Studio or the Build Tools with the C++ workload.' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'No Visual Studio installation with the C++ tools found.' }
$vcvars = Join-Path $vs 'VC/Auxiliary/Build/vcvarsall.bat'

$hostArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$target = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
$arch = if ($hostArch -eq $target) { $target } else { "${hostArch}_$target" }

$lsr = Join-Path $root 'native/msaudio/libsamplerate'
$src = (@((Join-Path $root 'native/msaudio/msaudio.c')) + ('samplerate.c', 'src_sinc.c', 'src_linear.c', 'src_zoh.c' | ForEach-Object { Join-Path $lsr $_ }) | ForEach-Object { "`"$_`"" }) -join ' '
$obj = Join-Path $Out 'obj'
New-Item -ItemType Directory -Force -Path $obj | Out-Null
$dll = Join-Path $Out 'msaudio.dll'
$cmd = "`"$vcvars`" $arch >nul && cl /nologo /O2 /W3 /LD /MT /DHAVE_CONFIG_H /D_CRT_SECURE_NO_WARNINGS /Fo`"$obj\\`" $src /link /OUT:`"$dll`" ole32.lib"
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw "cl failed ($LASTEXITCODE)" }
Remove-Item -Recurse -Force $obj
Get-ChildItem $Out -Exclude 'msaudio.dll' | Remove-Item -Force
Write-Host "built $dll"
