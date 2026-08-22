# tools/build_release_extras.ps1
# v1.0.0 release: emit helper files into AppDir.
# Uses @''@ single-quoted here-strings (no backtick escape needed, no $ interpolation)
# then substitutes {VERSION} placeholder manually for the two places we need it.
# NOTE: this file MUST be saved UTF-8 with BOM for PowerShell 5.1 to correctly
# parse here-strings that contain non-ASCII (Chinese) characters.
param(
    [Parameter(Mandatory=$true)][string]$AppDir,
    [Parameter(Mandatory=$true)][string]$Version
)
$ErrorActionPreference = "Stop"

# --- README.txt ---
Write-Host "[extras] writing README.txt..." -ForegroundColor Yellow
$readme = @'
# ComfyUIManagement v{VERSION}

绿色版 WPF 应用,免安装。

## 快速开始

1. 解压到任意目录(如 `D:\Tools\ComfyUIManagement\`)
2. 双击 `ComfyUI.Manager.exe`
3. 首次启动会弹出配置向导(选择安装根目录 + Python 解释器)
4. 配置完成后进入主界面

## 目录说明

| 目录 | 说明 |
|---|---|
| `python/` | 内置 portable Python(用于创建环境)|
| `bin/git-portable/` | 内置 git(用于拉取节点仓库)|
| `ComfyUI/` | ComfyUI 源模板 |
| `data/` | 节点详情缓存(catalog-cache.db) |
| `logs/` | 运行日志 |

用户配置后会在所选安装根目录下创建:`envs\`(环境)、`local-nodes\`(本地节点)、
`workflows\`(工作流)、`models\`(模型)等子目录。

## 工作流 / 模型市场

v1.0.0 暂不提供,将在后续版本发布。侧栏对应按钮为灰色不可用。

## 卸载

双击运行 `uninstall.bat`(只删除应用目录 + 配置 sentinel,**不删除**用户数据)。
如需清理用户数据,请手动删除安装根目录下的 `.manager\` 子目录(首次启动向导中所选的安装根目录)。

## 创建开始菜单快捷方式

双击 `install-start-menu.bat`。
'@
$readme -replace '\{VERSION\}', $Version |
    Set-Content (Join-Path $AppDir "README.txt") -Encoding UTF8

# --- uninstall.bat ---
Write-Host "[extras] writing uninstall.bat..." -ForegroundColor Yellow
$uninstall = @'
@echo off
setlocal
echo 即将卸载 ComfyUIManagement ...
echo.
echo 该脚本会删除:
echo   - 当前应用目录(包含 exe + python + git-portable)
echo   - %%APPDATA%%\ComfyUI-Manager\.first-run-complete
echo.
echo 不会删除:
echo   - %%APPDATA%%\ComfyUI-Manager\settings.json(用户配置)
echo   - 安装根目录下创建的 envs\workflows\models 等用户数据
echo.
set /p CONFIRM=确认卸载?(Y/N)
if /i not "%CONFIRM%"=="Y" goto :end
cd /d "%~dp0"
rd /s /q "%~dp0"
if exist "%~dp0.manager\.first-run-complete" del /q "%~dp0.manager\.first-run-complete"
echo.
echo 卸载完成。
:end
pause
'@
# Use UTF8 (without BOM) — modern CMD supports it under chcp 65001.
# The brief's "-Encoding ASCII" would strip Chinese to "?" — corrected here.
$uninstall | Set-Content (Join-Path $AppDir "uninstall.bat") -Encoding UTF8

# --- install-start-menu.bat ---
Write-Host "[extras] writing install-start-menu.bat..." -ForegroundColor Yellow
$startmenu = @'
@echo off
setlocal
set EXEDIR=%~dp0
set SHORTCUT=%APPDATA%\Microsoft\Windows\Start Menu\Programs\ComfyUIManagement.lnk
set TARGET=%EXEDIR%ComfyUI.Manager.exe
powershell -NoProfile -Command "$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('%SHORTCUT%'); $s.TargetPath = '%TARGET%'; $s.WorkingDirectory = '%EXEDIR%'; $s.Description = 'ComfyUIManagement v{VERSION}'; $s.Save()"
echo 开始菜单快捷方式已创建:%SHORTCUT%
pause
'@
$startmenu -replace '\{VERSION\}', $Version |
    Set-Content (Join-Path $AppDir "install-start-menu.bat") -Encoding UTF8

Write-Host "[extras] done" -ForegroundColor Green
