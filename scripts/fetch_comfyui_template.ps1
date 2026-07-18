# scripts/fetch_comfyui_template.ps1
# 把 ComfyUI 源 shallow-clone 到 <repo>/ComfyUI/,作为 "shared" 布局 env 的
# 模板源(替代 v0.6.3 之前的 "用户必须自己 git clone" 步骤)。
# 使用 bundled portable git (bin/git-portable/cmd/git.exe),不依赖系统 PATH。
# 幂等:目录已存在 + main.py 存在 → 跳过。

param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/.."),
    [string]$Ref = "master",
    [string]$RemoteUrl = "https://github.com/comfyanonymous/ComfyUI.git"
)

$ErrorActionPreference = "Stop"
$ComfyUiDir = Join-Path $ProjectRoot "ComfyUI"
$GitPortableExe = Join-Path $ProjectRoot "bin/git-portable/cmd/git.exe"

# 1. 选 git exe(优先 portable,fallback 到 PATH)
$GitExe = if (Test-Path $GitPortableExe) {
    $GitPortableExe
} else {
    Write-Warning "[warn] bin/git-portable/cmd/git.exe not found; falling back to system 'git'."
    "git"
}

# 2. 幂等:目录存在 + main.py 存在 → 跳过
if ((Test-Path $ComfyUiDir) -and (Test-Path (Join-Path $ComfyUiDir "main.py"))) {
    $existing = & $GitExe -C $ComfyUiDir rev-parse --short HEAD 2>&1
    Write-Host "[skip] ComfyUI template already present at $ComfyUiDir (HEAD=$existing)" -ForegroundColor DarkGray
    return
}

# 3. 目录存在但内容不对(比如上次 fetch 中断) → 清掉重拉
if (Test-Path $ComfyUiDir) {
    Write-Host "[clean] Removing stale $ComfyUiDir" -ForegroundColor Yellow
    Remove-Item -Recurse -Force $ComfyUiDir
}

# 4. shallow clone
Write-Host "[fetch] Cloning $RemoteUrl ($Ref) → $ComfyUiDir" -ForegroundColor Yellow
& $GitExe clone --depth 1 --branch $Ref $RemoteUrl $ComfyUiDir
if ($LASTEXITCODE -ne 0) {
    throw "git clone failed (exit=$LASTEXITCODE)"
}

# 5. 验证
$MainPy = Join-Path $ComfyUiDir "main.py"
if (-not (Test-Path $MainPy)) {
    throw "Clone did not produce expected main.py at $MainPy — refusing to ship"
}

$Head = & $GitExe -C $ComfyUiDir rev-parse --short HEAD 2>&1
$SizeBytes = (Get-ChildItem $ComfyUiDir -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host "[ok] Installed ComfyUI template: HEAD=$Head, $([math]::Round($SizeBytes/1MB, 1)) MB at $ComfyUiDir" -ForegroundColor Green
