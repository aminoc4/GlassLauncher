# GlassLauncher 原生版构建脚本（w64devkit / MinGW-w64 GCC）
# 用法：pwsh -File build.ps1 [-OutDir D:\GlassLauncher-handoff\.agent\native] [-Release]
param(
    [string]$OutDir = 'D:\GlassLauncher-handoff\.agent\native',
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'src'
$sources = Get-ChildItem $src -Filter *.cpp | ForEach-Object { $_.FullName }

New-Item -ItemType Directory $OutDir -Force | Out-Null
$exe = Join-Path $OutDir 'GlassLauncherNative.exe'

$flags = @(
    '-std=c++17', '-O2', '-s', '-static', '-municode', '-mwindows',
    '-DUNICODE', '-D_UNICODE', '-D_WIN32_WINNT=0x0A00', '-DWINVER=0x0A00',
    "-I$src"
)
$libs = @(
    '-ld2d1', '-ldwrite', '-ldwmapi', '-lwindowscodecs',
    '-luser32', '-lgdi32', '-lole32', '-luuid', '-lshell32', '-lshlwapi',
    '-lpsapi', '-ladvapi32', '-lcomctl32'
)

Write-Host "g++ $($sources.Count) sources -> $exe"
& g++ @flags @sources @libs -o $exe
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

$size = (Get-Item $exe).Length
Write-Host ("build ok: {0:N0} bytes" -f $size)
