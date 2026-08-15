# scripts/build_staging.ps1
# Mirror build_release.ps1 steps 5–6 (git-portable bundle) for staging builds.
# 幂等: fetch_git_portable.ps1 已存在则 skip; Copy-Item -Force 覆盖。
# 不动 dotnet publish 参数 — 跟现 staging publish 命令完全一致。
#
# 用法: scripts/build_staging.ps1  (从 repo root 跑)
# 输出: release/staging/ComfyUI Manager/ + bin/git-portable/ 子目录

param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/.."),
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "release/staging/ComfyUI Manager"
)

$ErrorActionPreference = "Stop"
$AppDir = Join-Path $ProjectRoot $OutputDir

Write-Host "[1/3] Ensuring git-portable..." -ForegroundColor Yellow
& "$ProjectRoot/scripts/fetch_git_portable.ps1" -ProjectRoot $ProjectRoot
if ($LASTEXITCODE -ne 0) { throw "fetch_git_portable.ps1 failed" }

Write-Host "[2/3] Publishing $Configuration $Runtime self-contained..." -ForegroundColor Yellow
dotnet publish "$ProjectRoot/src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj" `
    -c $Configuration -r $Runtime --self-contained `
    -p:PublishSingleFile=false `
    -o $AppDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "[3/3] Copying git-portable to staging output..." -ForegroundColor Yellow
$GitPortableSrc = Join-Path $ProjectRoot "bin/git-portable"
$GitPortableDst = Join-Path $AppDir "bin/git-portable"
if (Test-Path $GitPortableDst) { Remove-Item -Recurse -Force $GitPortableDst }
Copy-Item -Recurse -Force $GitPortableSrc $GitPortableDst

Write-Host "[ok] staging built at $AppDir with bundled git-portable" -ForegroundColor Green
& "$GitPortableDst/cmd/git.exe" --version
