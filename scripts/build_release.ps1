# scripts/build_release.ps1
# M5.2 release build — WPF-only zip, no Python service.
# Output: release/ComfyUI-Manager-v0.6.0-win-x64.zip
param(
    [string]$Version = "0.6.0",
    [string]$OutputDir = "release"
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path "$PSScriptRoot/.."
$StageDir = Join-Path $Root "$OutputDir/staging"
$ZipPath = Join-Path $Root "$OutputDir/ComfyUI-Manager-v$Version-win-x64.zip"

Write-Host "=== M5.2 release build v$Version (WPF-only) ===" -ForegroundColor Cyan

# 1. 清理 staging
Write-Host "[1/7] Cleaning staging..." -ForegroundColor Yellow
if (Test-Path $StageDir) { Remove-Item -Recurse -Force $StageDir }
New-Item -ItemType Directory -Path $StageDir | Out-Null
$AppDir = Join-Path $StageDir "ComfyUI Manager"
New-Item -ItemType Directory -Path $AppDir | Out-Null

# 2. dotnet publish self-contained
Write-Host "[2/7] Publishing WPF..." -ForegroundColor Yellow
$PublishDir = Join-Path $Root "src-wpf/ComfyUI.Manager/bin/Release/net8.0-windows/win-x64/publish"
if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
dotnet publish "$Root/src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -o $PublishDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 3. 复制 WPF 输出到 AppDir
Write-Host "[3/7] Copying WPF output..." -ForegroundColor Yellow
Copy-Item -Recurse -Force "$PublishDir/*" $AppDir

# 4. 复制 portable Python(venv verifier + 包安装路径走这个)
Write-Host "[4/7] Copying portable Python..." -ForegroundColor Yellow
if (-not (Test-Path "$Root/python")) {
    throw "portable python/ 目录不存在:需要在 venv 中跑过 comfy-mgr install 才能用 WPF 自检"
}
Copy-Item -Recurse -Force "$Root/python" (Join-Path $AppDir "python")

# 5. git-portable:缺失则 fetch,再复制到 zip
Write-Host "[5/7] Ensuring git-portable..." -ForegroundColor Yellow
if (-not (Test-Path "$Root/bin/git-portable/cmd/git.exe")) {
    Write-Host "  git-portable missing, fetching..." -ForegroundColor Yellow
    & "$Root/scripts/fetch_git_portable.ps1" -ProjectRoot $Root
    if ($LASTEXITCODE -ne 0) { throw "fetch_git_portable.ps1 failed" }
}
New-Item -ItemType Directory -Path (Join-Path $AppDir "bin") | Out-Null
Copy-Item -Recurse -Force "$Root/bin/git-portable" (Join-Path $AppDir "bin/git-portable")

# 5.5: fetch ComfyUI source template(幂等)
Write-Host "[5.5/7] Ensuring ComfyUI template..." -ForegroundColor Yellow
if (-not (Test-Path "$Root/ComfyUI/main.py")) {
    Write-Host "  ComfyUI template missing, fetching..." -ForegroundColor Yellow
    & "$Root/scripts/fetch_comfyui_template.ps1" -ProjectRoot $Root
    if ($LASTEXITCODE -ne 0) { throw "fetch_comfyui_template.ps1 failed" }
}
if (-not (Test-Path (Join-Path $AppDir "ComfyUI"))) {
    New-Item -ItemType Directory -Path (Join-Path $AppDir "ComfyUI") -Force | Out-Null
}
# copy with overwrite so re-runs stay clean
robocopy "$Root/ComfyUI" (Join-Path $AppDir "ComfyUI") /MIR /NJH /NJS /NDL /NFL /NC /NS | Out-Null

# 6. docs + logs dir + zip
Write-Host "[7/7] Finalizing + compressing..." -ForegroundColor Yellow
Copy-Item -Force "$Root/README.md" $AppDir
if (Test-Path "$Root/LICENSE") { Copy-Item -Force "$Root/LICENSE" $AppDir }
$LogsDir = Join-Path $AppDir "logs"
New-Item -ItemType Directory -Path $LogsDir | Out-Null
"" | Set-Content (Join-Path $LogsDir ".gitkeep")

if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }
Compress-Archive -Path "$AppDir/*" -DestinationPath $ZipPath -CompressionLevel Optimal

$Size = (Get-Item $ZipPath).Length / 1MB
Write-Host "✓ Built $ZipPath ($([math]::Round($Size, 1)) MB)" -ForegroundColor Green
Write-Host "Unzip and run 'ComfyUI Manager.exe' to test." -ForegroundColor Green