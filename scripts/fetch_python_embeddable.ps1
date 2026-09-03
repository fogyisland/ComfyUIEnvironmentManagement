# scripts/fetch_python_embeddable.ps1
# Download Python 3.x embedded Windows distribution (~30MB) into release/python-embed/.
# 比 user dev `python/` 小很多(只含 python.exe + stdlib,site-packages 留空,
# env-create 自己管 venv)。Network 拦截 python.org / aliyun / huaweicloud,试多个 URL
# 直到一个 work(用户网络环境通常 1-2 个可达)。
#
# v1.0.0.x T36:per user 决策"保证 release 尽可能小,我记得 Python 网上的包只有 30M"
# —— 用官方 embeddable distribution 替代 user dev `python/`(69M 含 site-packages + Doc + Tcl)
#
# 用法: scripts/fetch_python_embeddable.ps1
# 输出: release/python-embed/python-3.10.11-embed-amd64.zip + release/python-embed/extracted/

param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/.."),
    [string]$PythonVersion = "3.10.11",
    [string]$OutputDir = "release/python-embed"
)

$ErrorActionPreference = "Stop"
$ZipPath = Join-Path $ProjectRoot "$OutputDir/python-$PythonVersion-embed-amd64.zip"
$ExtractDir = Join-Path $ProjectRoot "$OutputDir/extracted"
New-Item -ItemType Directory -Path (Join-Path $ProjectRoot $OutputDir) -Force | Out-Null

$Urls = @(
    "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip",
    "https://mirrors.aliyun.com/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip",
    "https://mirrors.huaweicloud.com/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip",
    "https://mirrors.tuna.tsinghua.edu.cn/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip"
)

$Downloaded = $false
foreach ($u in $Urls) {
    Write-Host "Try: $u" -ForegroundColor Yellow
    try {
        $wc = New-Object System.Net.WebClient
        $wc.DownloadFile($u, $ZipPath)
        $size = (Get-Item $ZipPath).Length
        Write-Host ("  -> {0:N1} MB" -f ($size/1MB)) -ForegroundColor DarkGray
        if ($size -gt 20MB) {
            $Downloaded = $true
            Write-Host "  ✓ valid size" -ForegroundColor Green
            break
        } else {
            Remove-Item -Force $ZipPath -ErrorAction SilentlyContinue
        }
    } catch {
        Write-Host ("  -> FAIL: " + $_.Exception.Message) -ForegroundColor Red
        Remove-Item -Force $ZipPath -ErrorAction SilentlyContinue
    }
}

if (-not $Downloaded) {
    throw "所有 mirror 都 fail。手动从 https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip 下载并放到: $ZipPath"
}

# 解压
Write-Host "Extracting to $ExtractDir..." -ForegroundColor Yellow
if (Test-Path $ExtractDir) { Remove-Item -Recurse -Force $ExtractDir }
Expand-Archive -Path $ZipPath -DestinationPath $ExtractDir

Write-Host ""
Write-Host "=== Done: $ExtractDir ===" -ForegroundColor Green
Write-Host "  build_staging_extended.ps1 step 3 自动检测 $ExtractDir 优先用" -ForegroundColor DarkGray
