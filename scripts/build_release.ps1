# scripts/build_release.ps1
# v1.0.0 release build — WPF-only zip, no Python service.
# Output: release/ComfyUIManagement-v1.0.0-win-x64.zip
#
# v1.0.0 目录结构重构 — 顶层目录含义:
#   1. <root>                  = app exe + .dll(根目录就是应用)
#   2. <root>/ComfyUITemplate  = ComfyUI 源模板(shared 布局,供新建 env 时复制/junction)
#   3. <root>/Python           = portable Python(保证 venv 能起来)
#   4. <root>/Embeded    = git-portable 等内嵌工具
#   5. <root>/Workflow   = 工作流市场下载目录(运行期自动创建)
#   7. <root>/Envs       = 用户创建的环境
#   9. <root>/languages  = 卫星资源 DLL(每个 culture 一个子目录)
#  10. <root>/Logs       = 应用日志
#  11. <root>/Data       = catalog-cache.db(随包发布,走预填)
#  12. <root>/assets     = icon / splash / receiveMark.jpg
param(
    [string]$Version = "1.0.0",
    [string]$OutputDir = "release"
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path "$PSScriptRoot/.."
$StageDir = Join-Path $Root "$OutputDir/staging"
$ZipPath = Join-Path $Root "$OutputDir/ComfyUIManagement-v$Version-win-x64.zip"

Write-Host "=== v1.0.0 release build v$Version (WPF-only) ===" -ForegroundColor Cyan

# 1. 清理 staging
Write-Host "[1/8] Cleaning staging..." -ForegroundColor Yellow
if (Test-Path $StageDir) {
    # 容忍 Defender 扫描时锁住的大模型文件 —— robocopy /MIR 会负责把 ComfyUITemplate/ 同步到源状态
    Remove-Item -Recurse -Force $StageDir -ErrorAction SilentlyContinue
    # 再尝试清理任何残留目录(可能因为锁而被跳过)
    if (Test-Path $StageDir) { Remove-Item -Recurse -Force $StageDir -ErrorAction SilentlyContinue }
}
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null
$AppDir = Join-Path $StageDir "ComfyUIManagement"
New-Item -ItemType Directory -Path $AppDir -Force | Out-Null

# 2. dotnet publish self-contained
Write-Host "[2/8] Publishing WPF..." -ForegroundColor Yellow
$PublishDir = Join-Path $Root "src-wpf/ComfyUI.Manager/bin/Release/net8.0-windows/win-x64/publish"
if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
dotnet publish "$Root/src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -o $PublishDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 3. 复制 WPF 输出到 AppDir
Write-Host "[3/8] Copying WPF output..." -ForegroundColor Yellow
Copy-Item -Recurse -Force "$PublishDir/*" $AppDir

# 4. 移动 satellite resource assemblies → languages/<culture>/
# (publish 输出默认把 satellite DLLs 散在 publish/<culture>/ 下;
#  v1.0.0 顶层目录结构规定语言放 languages/ 下,AppDomain.AssemblyResolve 钩子会
#  把 .NET 默认查找路径重定向到这里 — 见 App.xaml.cs ResolveSatelliteAssemblyFromLanguagesDir)
Write-Host "[4/8] Moving satellite resource assemblies to languages/..." -ForegroundColor Yellow
$LanguagesDir = Join-Path $AppDir "languages"
New-Item -ItemType Directory -Path $LanguagesDir -Force | Out-Null
Get-ChildItem -Path $AppDir -Directory | Where-Object {
    # 卫星 culture 子目录(BCP 47 形态):"zh" / "zh-CN" / "en-US" / "zh-Hans" / "zh-Hant"
    # — language(2 lowercase) + 可选 script([A-Z][a-z]{3},e.g. Hans/Hant/Cyrl)
    #   或 region(2 uppercase 或 3 digits,e.g. CN/TW/419)
    # 跳过非 culture 顶层目录(Embeded/Python/ComfyUITemplate 等)
    $_.Name -match '^[a-z]{2}(-[A-Z][a-z]{3}|-[A-Z]{2}|-[0-9]{3})?$'
} | ForEach-Object {
    $cultureDir = $_.FullName
    $targetDir = Join-Path $LanguagesDir $_.Name
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    Get-ChildItem -Path $cultureDir -File | Move-Item -Destination $targetDir -Force
    Remove-Item -Path $cultureDir -Recurse -Force
    Write-Host "  moved $($_.Name)/ → languages/$($_.Name)/" -ForegroundColor DarkGray
}

# 5. 复制 portable Python(v1.0.0:python/ → Python/)
Write-Host "[5/8] Copying portable Python..." -ForegroundColor Yellow
if (-not (Test-Path "$Root/python")) {
    throw "portable python/ 目录不存在:需要在 venv 中跑过 comfy-mgr install 才能用 WPF 自检"
}
Copy-Item -Recurse -Force "$Root/python" (Join-Path $AppDir "Python")

# 6. git-portable:缺失则 fetch,再复制到 zip(v1.0.0:bin/ → Embeded/)
Write-Host "[6/8] Ensuring git-portable..." -ForegroundColor Yellow
if (-not (Test-Path "$Root/bin/git-portable/cmd/git.exe")) {
    Write-Host "  git-portable missing, fetching..." -ForegroundColor Yellow
    & "$Root/scripts/fetch_git_portable.ps1" -ProjectRoot $Root
    if ($LASTEXITCODE -ne 0) { throw "fetch_git_portable.ps1 failed" }
}
New-Item -ItemType Directory -Path (Join-Path $AppDir "Embeded") -Force | Out-Null
Copy-Item -Recurse -Force "$Root/bin/git-portable" (Join-Path $AppDir "Embeded/git-portable")

# 6.5: fetch ComfyUI source template(幂等)
# v1.0.0+:模板目录从 ComfyUI/ → ComfyUITemplate/(避免用户误以为是"已安装的 ComfyUI")
Write-Host "[6.5/8] Ensuring ComfyUI template..." -ForegroundColor Yellow
if (-not (Test-Path "$Root/ComfyUITemplate/main.py")) {
    Write-Host "  ComfyUI template missing, fetching..." -ForegroundColor Yellow
    & "$Root/scripts/fetch_comfyui_template.ps1" -ProjectRoot $Root
    if ($LASTEXITCODE -ne 0) { throw "fetch_comfyui_template.ps1 failed" }
}
if (-not (Test-Path (Join-Path $AppDir "ComfyUITemplate"))) {
    New-Item -ItemType Directory -Path (Join-Path $AppDir "ComfyUITemplate") -Force | Out-Null
}
# copy with overwrite so re-runs stay clean
# 排除用户的本地数据(models/output/input/cache/custom_nodes 等),只保留 ComfyUI 源码作为模板
# /XD = exclude directories(空格分隔列表)
robocopy "$Root/ComfyUITemplate" (Join-Path $AppDir "ComfyUITemplate") /MIR `
    /XD models output input __pycache__ custom_nodes localnodes user temp .git `
    /XF "*.pyc" "*.safetensors" "*.ckpt" "*.pt" "*.pth" "*.bin" "*.gguf" `
    /NJH /NJS /NDL /NFL /NC /NS | Out-Null

# 7. 预填 catalog-cache.db(v1.0.0:data/ → Data/)
Write-Host "[7/8] Pre-filling catalog-cache.db..." -ForegroundColor Yellow
$AppDataDir = Join-Path $AppDir "Data"
New-Item -ItemType Directory -Path $AppDataDir -Force | Out-Null
$CatalogDb = Join-Path $AppDataDir "catalog-cache.db"
if (-not (Test-Path $CatalogDb) -or $env:REBUILD_CATALOG -eq "1") {
    # python 把进度写到 stderr,PowerShell native-command mode 会把 stderr 当 ErrorRecord
    # 在 $ErrorActionPreference=Stop 下第一条 stderr 行就终止脚本
    # 临时切到 Continue 避免触发 Stop,然后用 cmd.exe 调 python 完全绕过 PS error classification
    $prevPref = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    cmd.exe /c "python `"$Root/scripts/prefill_catalog_cache.py`" `"$CatalogDb`" 2>&1"
    $pyExit = $LASTEXITCODE
    $ErrorActionPreference = $prevPref
    if ($pyExit -ne 0) { throw "prefill_catalog_cache.py failed (exit $pyExit)" }
    Write-Host "  catalog-cache.db pre-filled" -ForegroundColor DarkGray
} else {
    Write-Host "  catalog-cache.db exists, skipping (set REBUILD_CATALOG=1 to force)" -ForegroundColor DarkGray
}

# 7.5: v1.0.0 运行期 extras(README + uninstall + startmenu 快捷方式)
Write-Host "[7.5/8] Emitting extras..." -ForegroundColor Yellow
& "$Root/tools/build_release_extras.ps1" -AppDir $AppDir -Version $Version
if ($LASTEXITCODE -ne 0) { throw "build_release_extras.ps1 failed" }

# 8. 顶层目录放 .gitkeep 占位,保证解压后 13 个顶层目录都在(用户目录结构 spec 完整)
#    Workflow/Envs/Models/Nodes/LocalNodes 是运行期自动创建的空目录
Write-Host "[8/8] Finalizing + compressing..." -ForegroundColor Yellow
Copy-Item -Force "$Root/README.md" $AppDir
if (Test-Path "$Root/LICENSE") { Copy-Item -Force "$Root/LICENSE" $AppDir }

# 顶层目录 placeholder
$topDirs = @("Workflow", "Envs", "Models")
foreach ($d in $topDirs) {
    $dir = Join-Path $AppDir $d
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    "" | Set-Content (Join-Path $dir ".gitkeep")
}

if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }
Compress-Archive -Path "$AppDir/*" -DestinationPath $ZipPath -CompressionLevel Optimal

$Size = (Get-Item $ZipPath).Length / 1MB
Write-Host "✓ Built $ZipPath ($([math]::Round($Size, 1)) MB)" -ForegroundColor Green
Write-Host "Unzip and run 'ComfyUIManagement\ComfyUI.Manager.exe' to test." -ForegroundColor Green
