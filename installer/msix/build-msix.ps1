# Сборка пакета MSIX для Microsoft Store из папки dotnet publish.
#   pwsh installer\msix\build-msix.ps1 -SourceDir publish\Shadepane -Version 0.2.0 -OutDir store
# Нужен Windows SDK (makeappx.exe, makepri.exe) — на runner'ах GitHub windows-latest он есть.
# Пакет не подписан: Store подписывает его сам. Проверить локально — docs/STORE.md, «Проверка пакета».
param(
    [Parameter(Mandatory)] [string] $SourceDir,
    [Parameter(Mandatory)] [string] $Version,
    [string] $OutDir = "store"
)
$ErrorActionPreference = "Stop"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be X.Y.Z, got '$Version'" }
# Store требует четвёртое число 0.
$packageVersion = "$Version.0"

$tools = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
         Sort-Object FullName -Descending | Select-Object -First 1
if (-not $tools) { throw "Windows SDK not found: makeappx.exe is missing" }
$makeappx = $tools.FullName
$makepri = Join-Path $tools.DirectoryName "makepri.exe"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$layout = Join-Path $OutDir "layout"
if (Test-Path $layout) { Remove-Item -Recurse -Force $layout }
New-Item -ItemType Directory -Force $layout | Out-Null

Copy-Item -Recurse "$SourceDir\*" $layout
Copy-Item -Recurse (Join-Path $here "Assets") (Join-Path $layout "Assets")
$manifest = Get-Content -Raw (Join-Path $here "AppxManifest.xml")
$manifest = $manifest -replace 'Version="0\.0\.0\.0"', "Version=""$packageVersion"""
Set-Content -Path (Join-Path $layout "AppxManifest.xml") -Value $manifest -Encoding UTF8

# resources.pri: по нему Windows выбирает логотип под масштаб экрана (scale-200, targetsize-24…).
# Индексируем только Assets, а не сотни файлов приложения.
$priConfig = Join-Path $OutDir "priconfig.xml"
& $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri createconfig failed ($LASTEXITCODE)" }
$config = Get-Content -Raw $priConfig
$config = $config -replace 'startIndexAt="\\"', 'startIndexAt="\Assets"'
Set-Content -Path $priConfig -Value $config -Encoding UTF8
& $makepri new /pr $layout /cf $priConfig /mn (Join-Path $layout "AppxManifest.xml") /of (Join-Path $layout "resources.pri") /o
if ($LASTEXITCODE -ne 0) { throw "makepri new failed ($LASTEXITCODE)" }

$msix = Join-Path $OutDir "Shadepane-$Version-win-x64.msix"
& $makeappx pack /d $layout /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed ($LASTEXITCODE)" }
Write-Host "MSIX: $msix (package version $packageVersion)"
