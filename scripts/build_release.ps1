# scripts/build_release.ps1
# M4 release build script — runs on Windows.
# Output: release/ComfyUI-Manager-v0.4.0-win-x64.zip
param(
    [string]$Version = "0.4.0",
    [string]$OutputDir = "release"
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path "$PSScriptRoot/.."
$StageDir = Join-Path $Root "$OutputDir/staging"
$ZipPath = Join-Path $Root "$OutputDir/ComfyUI-Manager-v$Version-win-x64.zip"

Write-Host "=== M4 release build v$Version ===" -ForegroundColor Cyan

# 1. 清理
Write-Host "[1/7] Cleaning staging..." -ForegroundColor Yellow
if (Test-Path $StageDir) { Remove-Item -Recurse -Force $StageDir }
New-Item -ItemType Directory -Path $StageDir | Out-Null
$AppDir = Join-Path $StageDir "ComfyUI Manager"
New-Item -ItemType Directory -Path $AppDir | Out-Null

# 2. dotnet publish self-contained
Write-Host "[2/7] Publishing WPF..." -ForegroundColor Yellow
$PublishDir = Join-Path $Root "src-wpf/ComfyUI.Manager/bin/Release/net8.0-windows/win-x64/publish"
dotnet publish "$Root/src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -o $PublishDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 3. 复制 WPF 输出
Write-Host "[3/7] Copying WPF output..." -ForegroundColor Yellow
Copy-Item -Recurse -Force "$PublishDir/*" $AppDir

# 4. 复制 Python 源码
Write-Host "[4/7] Copying Python source..." -ForegroundColor Yellow
Copy-Item -Recurse -Force "$Root/src" (Join-Path $AppDir "src")
Copy-Item -Recurse -Force "$Root/shared" (Join-Path $AppDir "shared")

# 5. 复制 portable deps(python + git)
Write-Host "[5/7] Copying portable deps..." -ForegroundColor Yellow
if (Test-Path "$Root/python") {
    Copy-Item -Recurse -Force "$Root/python" (Join-Path $AppDir "python")
}
if (Test-Path "$Root/bin/git-portable") {
    Copy-Item -Recurse -Force "$Root/bin/git-portable" (Join-Path $AppDir "bin/git-portable")
}

# 6. 复制脚本 + docs + logs
Write-Host "[6/7] Copying scripts + docs + logs dir..." -ForegroundColor Yellow
if (Test-Path "$Root/start-service.bat") {
    Copy-Item -Force "$Root/start-service.bat" $AppDir
}
if (Test-Path "$Root/run.bat") {
    # run.bat 是 M3 deprecated,但 spec §13.1 要求保留作为 legacy 提示
    Copy-Item -Force "$Root/run.bat" $AppDir
}
Copy-Item -Force "$Root/README.md" $AppDir
if (Test-Path "$Root/LICENSE") {
    Copy-Item -Force "$Root/LICENSE" $AppDir
}
$LogsDir = Join-Path $AppDir "logs"
New-Item -ItemType Directory -Path $LogsDir | Out-Null
"" | Set-Content (Join-Path $LogsDir ".gitkeep")

# 7. 打包 zip
Write-Host "[7/7] Compressing..." -ForegroundColor Yellow
if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }
Compress-Archive -Path "$AppDir/*" -DestinationPath $ZipPath -CompressionLevel Optimal

$Size = (Get-Item $ZipPath).Length / 1MB
Write-Host "✓ Built $ZipPath ($([math]::Round($Size, 1)) MB)" -ForegroundColor Green
Write-Host "Unzip and run 'ComfyUI Manager.exe' to test." -ForegroundColor Green
