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
# v1.0.0.x (2026-09-05):用户决策"删除 languages 里面除了中文和英文之外的其他语言" ——
# 只保留 zh* + en*(其余 10 个 culture 13 MB 节省 + 用户界面只显示中文/英文)。
# 注意 en 默认是 fallback culture,publish 输出没 en/ 目录(只 zh*),en 走 default Resources。
$KeepCultures = @("zh", "zh-CN", "zh-Hans", "zh-Hant", "en", "en-US")
Get-ChildItem -Path $AppDir -Directory | Where-Object {
    # 卫星 culture 子目录(BCP 47 形态):"zh" / "zh-CN" / "en-US" / "zh-Hans" / "zh-Hant"
    # — language(2 lowercase) + 可选 script([A-Z][a-z]{3},e.g. Hans/Hant/Cyrl)
    #   或 region(2 uppercase 或 3 digits,e.g. CN/TW/419)
    # 跳过非 culture 顶层目录(Embeded/Python/ComfyUITemplate 等)
    $_.Name -match '^[a-z]{2}(-[A-Z][a-z]{3}|-[A-Z]{2}|-[0-9]{3})?$'
} | ForEach-Object {
    if ($KeepCultures -contains $_.Name) {
        # keep → move to languages/
        $cultureDir = $_.FullName
        $targetDir = Join-Path $LanguagesDir $_.Name
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        Get-ChildItem -Path $cultureDir -File | Move-Item -Destination $targetDir -Force
        Remove-Item -Path $cultureDir -Recurse -Force
        Write-Host "  moved $($_.Name)/ → languages/$($_.Name)/" -ForegroundColor DarkGray
    } else {
        # not in keep list → delete (避免 cs/de/es/fr/it/ja/ko/pl/pt-BR/ru/tr 留顶层污染目录结构)
        $size = (Get-ChildItem $_.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
        Remove-Item -Path $_.FullName -Recurse -Force
        Write-Host ("  pruned culture: {0}/ ({1:N1} MB)" -f $_.Name, ($size/1MB))
    }
}

# 5. 复制 portable Python(v1.0.0:python/ → Python/)
Write-Host "[5/8] Copying portable Python..." -ForegroundColor Yellow
if (-not (Test-Path "$Root/python")) {
    throw "portable python/ 目录不存在:需要在 venv 中跑过 comfy-mgr install 才能用 WPF 自检"
}
Copy-Item -Recurse -Force "$Root/python" (Join-Path $AppDir "Python")

# 5.5. v1.0.0.x (2026-09-05) T41+:prune Python 副本的明显冗余(保留全部 stdlib 子目录)。
# 理由:env-create 流程用 venv 创建 venv + pip install,需要 stdlib 完整可用(运行时 import)。
# 只删以下非 runtime 大件:test 套件 / IDE / 文档 / 缓存 / 3rd-party site-packages。
# 实测 162 MB → ~45 MB(节省 ~117 MB),`python -m venv` + `pip --version` 验证通过。
Write-Host "[5.5/8] Pruning Python redundant dirs (keep all stdlib)..." -ForegroundColor Yellow
$PythonDst = Join-Path $AppDir "Python"
$PruneRedundant = @(
    # Lib/ 子目录(非 stdlib module,纯开发/测试/文档/缓存)
    "Lib/test",            # 60.9 MB - unit test 套件
    "Lib/__pycache__",     # 9.1 MB  - bytecode 缓存(首次运行自动重建)
    "Lib/site-packages",   # 17.8 MB - 3rd-party(venv 自管 site-packages)
    "Lib/idlelib",         # 4.5 MB  - IDLE IDE
    "Lib/unittest",        # 3.3 MB  - test framework(venv runtime 不用)
    "Lib/distutils",       # 2.6 MB  - deprecated(Python 3.12+ 移除)
    "Lib/tkinter",         # 2.1 MB  - GUI 工具包(venv 不用)
    "Lib/pydoc_data",      # 2.1 MB  - help 文档数据
    "Lib/lib2to3",         # 1.3 MB  - Python 2→3 converter(legacy)
    # 顶层目录
    "Doc",                 # 8.9 MB  - .chm 文档
    "tcl",                 # 6.7 MB  - Tcl/Tk 工具包(不用 tkinter GUI)
    "Scripts",             # 1.1 MB  - pre-built scripts(venv 内部自管)
    "include",             # 0.7 MB  - C headers(build extensions 用,runtime 不用)
    "Tools",               # 0.6 MB  - IDLE IDE
    "share"                # 0.4 MB  - locale data 备份
)
$SavedRedundant = 0
foreach ($sub in $PruneRedundant) {
    $p = Join-Path $PythonDst $sub
    if (Test-Path $p) {
        $size = (Get-ChildItem -Path $p -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
        Remove-Item -Recurse -Force $p -ErrorAction SilentlyContinue
        $SavedRedundant += $size
        Write-Host ("  pruned: $sub ({0:N1} MB)" -f ($size/1MB))
    }
}
Write-Host ("  total saved (redundant): {0:N1} MB" -f ($SavedRedundant/1MB))

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
    # v1.0.0.x (2026-09-05) T41+:prefill 失败不 throw,只 log warning ——
    # 公司网络常拦截 github raw(ltdrdata catalog fetch)。用户决策"先不跑节点的 prefill":
    # 让 build 继续到 step 8 zip,空 catalog-cache.db 让 C# 启动时 live fetch。
    # 设 FORCE_PREFILL=1 才强制 fail-fast(默认 skip-on-error,继续 build)。
    if ($pyExit -ne 0) {
        if ($env:FORCE_PREFILL -eq "1") {
            throw "prefill_catalog_cache.py failed (exit $pyExit)"
        }
        Write-Warning "prefill_catalog_cache.py failed (exit $pyExit) — skipping catalog pre-fill (set FORCE_PREFILL=1 to fail-fast)"
        Write-Warning "  C# runtime will live-fetch catalog on startup; release zip still buildable."
        # Remove partial db if any so C# can recreate schema cleanly
        if (Test-Path $CatalogDb) { Remove-Item -Force $CatalogDb -ErrorAction SilentlyContinue }
    } else {
        Write-Host "  catalog-cache.db pre-filled" -ForegroundColor DarkGray
    }
} else {
    Write-Host "  catalog-cache.db exists, skipping (set REBUILD_CATALOG=1 to force)" -ForegroundColor DarkGray
}

# 7.5: v1.0.0 运行期 extras(README + uninstall + startmenu 快捷方式)
Write-Host "[7.5/8] Emitting extras..." -ForegroundColor Yellow
& "$Root/tools/build_release_extras.ps1" -AppDir $AppDir -Version $Version
if ($LASTEXITCODE -ne 0) { throw "build_release_extras.ps1 failed" }

# 7.6: v1.0.0 sidebar.inf seed — release 包内置 <exeDir>/config/sidebar.inf,
# 跟 ui-preferences.json 同目录(发布 seed,非用户可变)。用户编辑后重启生效。
# 仓库根 config/sidebar.inf 是单一 source-of-truth,csproj 也用它拷到 dev 输出。
Write-Host "[7.6/8] Seeding config/sidebar.inf..." -ForegroundColor Yellow
$AppConfigDir = Join-Path $AppDir "config"
New-Item -ItemType Directory -Path $AppConfigDir -Force | Out-Null
Copy-Item -Force "$Root/config/sidebar.inf" (Join-Path $AppConfigDir "sidebar.inf")

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
if ($env:SKIP_ZIP -eq "1") {
    # v1.0.0.x (2026-09-05):用户原话"不需要压缩成zip" ——
    # 绿色软件绿色发布,staging 目录可直接 double-click ComfyUI.Manager.exe 跑,
    # 不需要分发 zip(下载后还要 unzip 一步,体验更差)。设 SKIP_ZIP=1 跳过 Compress-Archive。
    Write-Host "  SKIP_ZIP=1 — skipping Compress-Archive" -ForegroundColor DarkGray
    $Size = (Get-ChildItem $AppDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
    Write-Host "✓ Built $AppDir ($([math]::Round($Size, 1)) MB) — staging only (no zip)" -ForegroundColor Green
    Write-Host "Run 'ComfyUIManagement\ComfyUI.Manager.exe' directly." -ForegroundColor Green
} else {
    Compress-Archive -Path "$AppDir/*" -DestinationPath $ZipPath -CompressionLevel Optimal
    $Size = (Get-Item $ZipPath).Length / 1MB
    Write-Host "✓ Built $ZipPath ($([math]::Round($Size, 1)) MB)" -ForegroundColor Green
    Write-Host "Unzip and run 'ComfyUIManagement\ComfyUI.Manager.exe' to test." -ForegroundColor Green
}
