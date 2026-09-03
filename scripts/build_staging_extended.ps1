# scripts/build_staging_extended.ps1
# v1.0.0.x T35:扩展 build_staging.ps1,加 Python + ComfyUITemplate + localnodes + Workflow,
# 不 zip。User 决策(2026-09-03):
# - 1. 模板需要保留: ComfyUITemplate/(从仓库根,排除 models/output/input/__pycache__/custom_nodes/localnodes/user/temp/.git + *.pyc *.safetensors *.ckpt *.pt *.pth *.bin *.gguf)
# - 2. 常用本地目录文件需要保留: localnodes/ + Workflow/ 实际文件拷入
# - 3. 其它(models/envs/python 不要:Python 仍拷,是 release runtime 需要)
#
# 输出: release/staging/ComfyUI Manager/  目录(不 zip)
# 用途: dev 试跑 + 验证 release 结构 + 准备用户私人数据(localnodes/workflow)

param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/.."),
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "release/staging/ComfyUI Manager"
)

$ErrorActionPreference = "Stop"
$AppDir = Join-Path $ProjectRoot $OutputDir

Write-Host "=== T35 staging build (extended, no zip) ===" -ForegroundColor Cyan
Write-Host "Output: $AppDir" -ForegroundColor DarkCyan

# 1. 清理 staging(忽略锁住文件错误,Defender 扫描时偶尔)
Write-Host "[1/7] Cleaning staging..." -ForegroundColor Yellow
if (Test-Path $AppDir) {
    Remove-Item -Recurse -Force $AppDir -ErrorAction SilentlyContinue
    if (Test-Path $AppDir) { Remove-Item -Recurse -Force $AppDir -ErrorAction SilentlyContinue }
}
New-Item -ItemType Directory -Path $AppDir -Force | Out-Null

# 2. dotnet publish self-contained (WPF)
Write-Host "[2/7] Publishing WPF self-contained..." -ForegroundColor Yellow
& "$ProjectRoot/scripts/build_staging.ps1" -ProjectRoot $ProjectRoot -Configuration $Configuration -Runtime $Runtime -OutputDir $OutputDir
if ($LASTEXITCODE -ne 0) { throw "build_staging.ps1 failed" }

# 3. portable Python (跟 build_release step 5 一样)
Write-Host "[3/7] Copying portable Python..." -ForegroundColor Yellow
if (-not (Test-Path "$ProjectRoot/python")) {
    throw "portable python/ 目录不存在:需要在 venv 中跑过 comfy-mgr install 才能用 WPF 自检"
}
Copy-Item -Recurse -Force "$ProjectRoot/python" (Join-Path $AppDir "Python")

# 3.5. 删 staging Python 副本的冗余 subdirs (per User 决策 T35 C 方案) ——
# 只删 staging 副本,user `python/` 实际数据不动
# - Lib/test/ (60.9M) 单元测试套件 runtime 不需要
# - Lib/idlelib/ (4.5M) IDLE GUI IDE runtime 不需要
# - Lib/unittest/ (3.3M) 测试框架
# - Lib/ensurepip/ (3.2M) pip installer runtime 不需要(用户用 venv)
# - Lib/distutils/ (2.6M) deprecation
# - Lib/__pycache__/ (9.1M) 字节码缓存,首运行会自动重建
# - Lib/site-packages/ (17.8M) pip packages,env-create 自己管 venv 不需要预装
Write-Host "[3.5/7] Pruning staging Python redundant subdirs..." -ForegroundColor Yellow
$PythonDst = Join-Path $AppDir "Python"
$PruneSubdirs = @(
    "Lib/test",
    "Lib/idlelib",
    "Lib/unittest",
    "Lib/ensurepip",
    "Lib/distutils",
    "Lib/__pycache__",
    "Lib/site-packages"
)
$Saved = 0
foreach ($sub in $PruneSubdirs) {
    $p = Join-Path $PythonDst $sub
    if (Test-Path $p) {
        $size = (Get-ChildItem -Path $p -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
        Remove-Item -Recurse -Force $p -ErrorAction SilentlyContinue
        $Saved += $size
        Write-Host ("  pruned: $sub ({0:N1} MB)" -f ($size/1MB))
    }
}
Write-Host ("  total saved: {0:N1} MB" -f ($Saved/1MB))

# 4. ComfyUITemplate/(从仓库根,排除大文件 + user 数据) — User 决策 "1. 模板需要保留"
Write-Host "[4/7] Copying ComfyUITemplate (excl models/output/input/custom_nodes/localnodes/user/...)..." -ForegroundColor Yellow
$ComfyUITemplateSrc = Join-Path $ProjectRoot "ComfyUITemplate"
$ComfyUITemplateDst = Join-Path $AppDir "ComfyUITemplate"
if (Test-Path "$ComfyUITemplateSrc/main.py") {
    if (Test-Path $ComfyUITemplateDst) { Remove-Item -Recurse -Force $ComfyUITemplateDst }
    robocopy $ComfyUITemplateSrc $ComfyUITemplateDst /MIR `
        /XD models output input __pycache__ custom_nodes localnodes user temp .git `
        /XF "*.pyc" "*.safetensors" "*.ckpt" "*.pt" "*.pth" "*.bin" "*.gguf" `
        /NJH /NJS /NDL /NFL /NC /NS | Out-Null
    Write-Host "  ComfyUITemplate copied (env-create base source)" -ForegroundColor DarkGray
} else {
    Write-Host "  ComfyUITemplate 不存在 (git fetch 失败,网络问题),创建 .gitkeep placeholder" -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $ComfyUITemplateDst -Force | Out-Null
    "" | Set-Content (Join-Path $ComfyUITemplateDst ".gitkeep")
    Write-Host "  re-run: scripts/fetch_comfyui_template.ps1 后再 build" -ForegroundColor DarkGray
}

# 5. localnodes/(用户本地常用节点) — User 决策 "2. localnodes 和 workflow 拷贝进去"
Write-Host "[5/7] Copying localnodes/..." -ForegroundColor Yellow
$LocalNodesSrc = Join-Path $ProjectRoot "localnodes"
if (Test-Path $LocalNodesSrc) {
    $LocalNodesDst = Join-Path $AppDir "localnodes"
    if (Test-Path $LocalNodesDst) { Remove-Item -Recurse -Force $LocalNodesDst }
    Copy-Item -Recurse -Force $LocalNodesSrc $LocalNodesDst
    Write-Host "  localnodes/ copied (user local nodes)" -ForegroundColor DarkGray
} else {
    Write-Host "  localnodes/ 不存在(用户还没装本地节点),跳过" -ForegroundColor DarkGray
}

# 6. Workflow/(用户下载的 workflow) — User 决策
Write-Host "[6/7] Copying Workflow/..." -ForegroundColor Yellow
$WorkflowSrc = Join-Path $ProjectRoot "Workflow"
if (Test-Path $WorkflowSrc) {
    $WorkflowDst = Join-Path $AppDir "Workflow"
    if (Test-Path $WorkflowDst) { Remove-Item -Recurse -Force $WorkflowDst }
    Copy-Item -Recurse -Force $WorkflowSrc $WorkflowDst
    Write-Host "  Workflow/ copied (user workflows)" -ForegroundColor DarkGray
} else {
    Write-Host "  Workflow/ 不存在(用户还没下载 workflow),跳过" -ForegroundColor DarkGray
}


# 7. 占位 .gitkeep 空目录(Envs / Models / logs 等运行期自动创建)
Write-Host "[7/7] Finalizing placeholder dirs..." -ForegroundColor Yellow
$topDirs = @("Envs", "Models", "logs", "Nodes")
foreach ($d in $topDirs) {
    $dir = Join-Path $AppDir $d
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    "" | Set-Content (Join-Path $dir ".gitkeep")
}

# 文件大小统计(per user 决策 "检查下文件总大小")
$TotalSize = (Get-ChildItem -Path $AppDir -Recurse -File -ErrorAction SilentlyContinue |
    Measure-Object -Property Length -Sum).Sum
$SizeMB = [math]::Round($TotalSize / 1MB, 1)
Write-Host ""
Write-Host "=== Done: $AppDir ($SizeMB MB) ===" -ForegroundColor Green
Write-Host "  WPF self-contained publish: included" -ForegroundColor DarkGray
Write-Host "  Portable Python: included" -ForegroundColor DarkGray
Write-Host "  git-portable: included (in publish output)" -ForegroundColor DarkGray
Write-Host "  ComfyUITemplate: included (excl models/output/input/etc)" -ForegroundColor DarkGray
Write-Host "  localnodes/ + Workflow/: included (user private data)" -ForegroundColor DarkGray
Write-Host "  Envs/, Models/, logs/, Nodes/: .gitkeep placeholder" -ForegroundColor DarkGray
Write-Host ""
Write-Host "DB vs files layout (per user 决策 3):" -ForegroundColor Cyan
Write-Host "  DB (state.db / settings.inf):" -ForegroundColor DarkGray
Write-Host "    - env list, status, port, PID" -ForegroundColor DarkGray
Write-Host "    - settings (paths, env-create options, user preferences)" -ForegroundColor DarkGray
Write-Host "  Files (disk):" -ForegroundColor DarkGray
Write-Host "    - env source code: Envs/<envId>/custom_nodes/" -ForegroundColor DarkGray
Write-Host "    - node source: Envs/<envId>/custom_nodes/<node> (per-env)" -ForegroundColor DarkGray
Write-Host "    - global nodes: Nodes/ or localnodes/ (per app config)" -ForegroundColor DarkGray
Write-Host "    - models: Models/ (env-create junction target)" -ForegroundColor DarkGray
Write-Host "    - workflows: Workflow/<file>.json" -ForegroundColor DarkGray
Write-Host "    - logs: logs/<envId>.log" -ForegroundColor DarkGray
Write-Host "    - templates: ComfyUITemplate/ (env-create base source)" -ForegroundColor DarkGray
