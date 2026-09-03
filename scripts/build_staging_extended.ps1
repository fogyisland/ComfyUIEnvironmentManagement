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
# v1.0.0.x T37 fix:之前 $ProjectRoot 在被 & 调 build_staging.ps1 子脚本时可能被 reset —
# line 44 `cd release/staging/...` 改变 working directory 跟 Reset-Location,
# 让 Resolve-Path 找不到 repo。重新 Resolve 一次确保 $ProjectRoot 跟 $SlimSrc 不为 null。
$ProjectRoot = (Resolve-Path "$PSScriptRoot/..")
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

# 2. dotnet publish self-contained (WPF) + slim git (替代 91M git-portable)
Write-Host "[2/7] Publishing WPF self-contained + slim git..." -ForegroundColor Yellow
& "$ProjectRoot/scripts/build_staging.ps1" -ProjectRoot $ProjectRoot -Configuration $Configuration -Runtime $Runtime -OutputDir $OutputDir
if ($LASTEXITCODE -ne 0) { throw "build_staging.ps1 failed" }

# 2.5. v1.0.0.x T37:per user 反馈"git 不是只有 30 多 M 吗" ——
# build_staging.ps1 拷的 bin/git-portable 是 git-for-windows 完整 bundle 91M(Avalonia GUI +
# gcmcore credential manager + perl + openssl + curl + ssh + tcl),git clone 实际只需要 git.exe +
# msys runtime + libcurl + libssl + libcrypto ~5-8MB
# 我们 *不* 改 build_staging.ps1(保持原纯净),而是在 staging build 跑完后覆盖 slim git
Write-Host "[2.5/7] Slimming git from 91M to ~8-12M (drop GUI/credential/perl/ssh/tcl)..." -ForegroundColor Yellow
$SlimSrc = Join-Path $ProjectRoot "release/git-slim"
$StagingGitDir = Join-Path $AppDir "bin/git-portable"
$SourceGitDir = Join-Path $ProjectRoot "bin/git-portable"
# 完整 git.exe 跑得动只依赖 system32 + 自带 mincore,不需要 mingw64/bin/* GUI DLL
# minimal subset: cmd/ + usr/bin/(msys runtime + libcurl + libssl + libcrypto + libiconv + git tools)
# exclude: mingw64/(GUI/gcmcore/credential) + perl + tcl + tk + ssh + rebase + awk + find + diff 等冗余
$SlimSrc = Join-Path $ProjectRoot "release/git-slim"
if (-not (Test-Path $SlimSrc)) { New-Item -ItemType Directory -Path $SlimSrc -Force | Out-Null }
$BuildSlimNow = $true
if (Test-Path (Join-Path $SlimSrc "cmd/git.exe")) {
    Write-Host "  using cached git-slim from $SlimSrc" -ForegroundColor DarkGray
    $BuildSlimNow = $false
} else {
    New-Item -ItemType Directory -Path $SlimSrc -Force | Out-Null
}
if ($BuildSlimNow) {
    # 拷 cmd/(git.exe + helpers 168K)
    $cmdSrc = Join-Path $SourceGitDir "cmd"
    $cmdDst = Join-Path $SlimSrc "cmd"
    if (Test-Path $cmdSrc) {
        New-Item -ItemType Directory -Path $cmdDst -Force | Out-Null
        Copy-Item -Recurse -Force $cmdSrc $cmdDst
    }
    # 拷 usr/bin/(msys runtime + libcurl + libssl + libcrypto + libiconv + 必要 git tools)
    # 关键:msys-2.0.dll(MSYS2 C runtime,2-3MB) + libcurl + libssl + libcrypto + libiconv
    # exclude: perl/* + tcl/* + tk/* + ssh* + ssh-* + awk + rebase + find + diff + grep + less
    $usrBinSrc = Join-Path $SourceGitDir "usr/bin"
    $usrBinDst = Join-Path $SlimSrc "usr/bin"
    if (Test-Path $usrBinSrc) {
        New-Item -ItemType Directory -Path $usrBinDst -Force | Out-Null
        $Excludes = @("perl*", "tcl*", "tk*", "ssh*", "rebase*", "awk*", "find*", "diff*", "grep*", "less*", "rsync*", "scp*", "sftp*", "cygpath*", "cygcheck*", "getfacl*", "setfacl*", "minTTY*", "winpty*", "bash*", "dash*", "env*", "expr*", "id*", "kill*", "ln*", "mkdir*", "mkfifo*", "mknod*", "mv*", "rm*", "rmdir*", "sleep*", "stty*", "sync*", "tee*", "touch*", "true*", "false*", "uname*", "wc*", "which*", "whoami*", "xargs*", "yes*", "cygserver*", "ld*", "cygcheck*", "mount*", "ps*", "kill*", "test*", "tr*", "cut*", "sort*", "uniq*", "head*", "tail*", "wc*", "paste*", "join*", "split*", "fmt*", "xargs*", "od*", "xxd*", "cksum*", "md5sum*", "sha1sum*", "sha256sum*", "sha512sum*", "sum*", "b2sum*", "base32*", "base64*", "md5sum*", "zip*", "unzip*", "tar*", "gzip*", "xz*", "bzip*", "compress*", "uncompress*", "lha*", "lzh*", "pax*", "cpio*", "ar*", "ranlib*", "nm*", "objdump*", "strings*", "strace*", "ltrace*", "gdb*", "addr2line*", "c++filt*", "elfedit*", "gprof*", "gcov*", "gdb-add-index*", "gprof*", "size*", "strings*", "objcopy*", "readelf*", "ldd*", "as*", "ar*", "gcc*", "g++*", "cc*", "cpp*", "c++*", "g77*", "f77*", "f95*", "fortran*", "gfortran*", "make*", "cmake*", "ctags*", "etags*", "cscope*", "gprof*", "gperf*", "time*", "timeout*", "date*", "cal*", "info*", "man*", "apropos*", "whatis*", "whereis*", "make*", "automake*", "autoconf*", "autoheader*", "autoreconf*", "autoscan*", "autoupdate*", "ifnames*", "symlinks*", "m4*", "gawk*", "awk*", "perl*", "pod2*", "podselect*", "prove*")
        $files = Get-ChildItem -Path $usrBinSrc -File | Where-Object { $name = $_.Name; -not ($Excludes | Where-Object { $name -like $_ }) }
        foreach ($f in $files) {
            Copy-Item -Force $f.FullName $usrBinDst
        }
    }
    # usr/libexec/(git helpers 560K)
    $libexecSrc = Join-Path $SourceGitDir "usr/libexec"
    $libexecDst = Join-Path $SlimSrc "usr/libexec"
    if (Test-Path $libexecSrc) {
        New-Item -ItemType Directory -Path $libexecDst -Force | Out-Null
        Copy-Item -Recurse -Force $libexecSrc $libexecDst
    }
    # etc/(gitconfig 1K)
    $etcSrc = Join-Path $SourceGitDir "etc"
    if (Test-Path $etcSrc) {
        $etcDst = Join-Path $SlimSrc "etc"
        New-Item -ItemType Directory -Path $etcDst -Force | Out-Null
        Copy-Item -Recurse -Force $etcSrc $etcDst
    }
    $SlimSize = (Get-ChildItem -Path $SlimSrc -Recurse -File | Measure-Object -Property Length -Sum).Sum
    Write-Host ("  git-slim cached: {0:N1} MB" -f ($SlimSize/1MB)) -ForegroundColor DarkGray
}
# 覆盖 staging bin/git-portable
if (Test-Path $StagingGitDir) { Remove-Item -Recurse -Force $StagingGitDir }
Copy-Item -Recurse -Force $SlimSrc $StagingGitDir
$StagingGitSize = (Get-ChildItem -Path $StagingGitDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("  staging bin/git-portable: 91M -> {0:N1} MB" -f ($StagingGitSize/1MB)) -ForegroundColor DarkGray

# 3. portable Python
# v1.0.0.x T36:per user 决策"保证 release 尽可能小,我记得 Python 网上的包只有 30M" ——
# 优先用 Python 官方 embeddable distribution(30MB,只 stdlib + interpreter,无 site-packages/Doc),
# fallback 到 user dev `python/`(~70-180MB 含 site-packages + Doc + Tcl + site-packages)。
Write-Host "[3/7] Copying portable Python..." -ForegroundColor Yellow
$PythonEmbeddedDir = Join-Path $ProjectRoot "release/python-embed/extracted"
if (Test-Path $PythonEmbeddedDir/python.exe) {
    # 优先:Python embeddable distribution(30MB,无冗余)
    $PythonSrc = $PythonEmbeddedDir
    Write-Host "  using Python embeddable from $PythonEmbeddedDir (small)" -ForegroundColor DarkGray
} elseif (Test-Path "$ProjectRoot/python") {
    # fallback:user dev python/(69-180MB)
    $PythonSrc = "$ProjectRoot/python"
    Write-Host "  using user dev python/(large; run scripts/fetch_python_embeddable.ps1 to get small)" -ForegroundColor DarkGray
} else {
    throw "找不到 Python source。跑 scripts/fetch_python_embeddable.ps1 下载 embeddable(30MB),或在 venv 中跑过 comfy-mgr install 让 user dev python/ 可用"
}
Copy-Item -Recurse -Force $PythonSrc (Join-Path $AppDir "Python")

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

# 3.6. v1.0.0.x T38:per user 决策"只需要能够运行,其他非必要内容都没有意义"
# 进一步删 Python runtime 不需要的:
# - Doc/ (8.9MB) Python 文档(.chm help file)
# - tcl/ (9MB) Tcl/Tk 工具包(不用 tkinter GUI 的话不需要)
# - Lib/ctypes/test/ (ctypes 测试套件)
# - Lib/importlib/test/ (importlib 测试)
# - DLLs/_test*.pyd (test C extensions,~几 MB)
# - include/ (1.1MB) Python C headers,build extensions 用,runtime 不需要
# 保留 stdlib 必备:Lib/{asyncio,collections,concurrent,ctypes,dbm,encodings,html,http,importlib,json,logging,multiprocessing,sqlite3,...}
Write-Host "[3.6/7] Pruning Python runtime bloat (Doc/tcl/tests/include)..." -ForegroundColor Yellow
$PruneRuntimeBloat = @(
    "Doc",
    "tcl",
    "Tools",
    "include",
    "DLLs/_ctypes_test.pyd",
    "DLLs/_testbuffer.pyd",
    "DLLs/_testcapi.pyd",
    "DLLs/_testconsole.pyd",
    "DLLs/_testimportmultiple.pyd",
    "DLLs/_testinternalcapi.pyd",
    "DLLs/_testmultiphase.pyd",
    "Lib/ctypes/test",
    "Lib/importlib/test",
    "Lib/sqlite3/test",
    "Lib/unittest/test",
    "Lib/test/support"
)
$SavedBloat = 0
foreach ($sub in $PruneRuntimeBloat) {
    $p = Join-Path $PythonDst $sub
    if (Test-Path $p) {
        $size = (Get-ChildItem -Path $p -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
        Remove-Item -Recurse -Force $p -ErrorAction SilentlyContinue
        $SavedBloat += $size
        Write-Host ("  pruned: $sub ({0:N1} MB)" -f ($size/1MB))
    }
}
Write-Host ("  total saved (runtime bloat): {0:N1} MB" -f ($SavedBloat/1MB))

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
