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

# 应用图标：把 WPF 版的 app.ico 编成资源 id 1（托盘/任务栏/窗口类共用）
$icon = Join-Path $PSScriptRoot '..\GlassLauncher\app.ico'
$iconRc = ($icon -replace '\\', '/')
$rcFile = Join-Path $OutDir 'app.rc'
$resFile = Join-Path $OutDir 'app.res'
if (Test-Path $icon) {
    "1 ICON `"$iconRc`"" | Set-Content $rcFile -Encoding ASCII
    & windres $rcFile -O coff -o $resFile
    if ($LASTEXITCODE -ne 0) { throw "windres failed ($LASTEXITCODE)" }
} else {
    Write-Host "warning: app.ico not found at $icon"
    $resFile = $null
}

$linkInputs = @()
if ($resFile) { $linkInputs += $resFile }

& g++ @flags @sources @linkInputs @libs -o $exe
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

$size = (Get-Item $exe).Length
Write-Host ("build ok: {0:N0} bytes" -f $size)
