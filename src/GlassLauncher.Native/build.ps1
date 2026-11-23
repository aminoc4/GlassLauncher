# GlassLauncher 原生版构建脚本（w64devkit / MinGW-w64 GCC）
# 用法：pwsh -File src/GlassLauncher.Native/build.ps1 [-OutDir <dir>]
param(
    [string]$OutDir = '',
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..\..'
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }
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

# 应用图标：编成资源 id 1（托盘/任务栏/窗口类共用）
$icon = Join-Path $root 'app.ico'
$iconRc = ($icon -replace '\\', '/')
$rcFile = Join-Path $OutDir 'app.rc'
$resFile = Join-Path $OutDir 'app.res'
if (Test-Path $icon) {
    @"
1 ICON "$iconRc"

1 VERSIONINFO
FILEVERSION 1,0,2,0
PRODUCTVERSION 1,0,2,0
BEGIN
  BLOCK "StringFileInfo"
  BEGIN
    BLOCK "040904b0"
    BEGIN
      VALUE "FileDescription", "GlassLauncher Native"
      VALUE "FileVersion", "1.0.2"
      VALUE "ProductName", "GlassLauncher"
      VALUE "ProductVersion", "1.0.2"
      VALUE "CompanyName", "aminoc4"
      VALUE "OriginalFilename", "GlassLauncherNative.exe"
    END
  END
  BLOCK "VarFileInfo"
  BEGIN
    VALUE "Translation", 0x409, 0x4b0
  END
END
"@ | Set-Content $rcFile -Encoding ASCII
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
