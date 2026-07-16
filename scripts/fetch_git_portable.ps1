# scripts/fetch_git_portable.ps1
# 下载 MinGit (官方 portable Git for Windows) 到 bin/git-portable/。
# 在 release zip 里作为 git.exe 替代,避免用户需要单独装 git。
# 幂等:已存在且能 --version 就跳过。

param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/.."),
    [string]$Version = "2.55.0.3"
)

$ErrorActionPreference = "Stop"
$BinDir = Join-Path $ProjectRoot "bin"
$GitPortableDir = Join-Path $BinDir "git-portable"
$GitExe = Join-Path $GitPortableDir "cmd/git.exe"

# 1. 已存在且能跑 → 直接跳过
if (Test-Path $GitExe) {
    try {
        $versionOutput = & $GitExe --version 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "[skip] git-portable already present: $versionOutput" -ForegroundColor DarkGray
            return
        }
    } catch {}
}

# 2. 下载 MinGit zip
$Tag = "v$(([Version]$Version).Major).$(([Version]$Version).Minor).$(([Version]$Version).Build).windows.3"
$ZipName = "MinGit-$Version-64-bit.zip"
$Url = "https://github.com/git-for-windows/git/releases/download/$Tag/$ZipName"
$TempZip = Join-Path $env:TEMP $ZipName

Write-Host "[fetch] Downloading $Url" -ForegroundColor Yellow
Invoke-WebRequest -Uri $Url -OutFile $TempZip -UseBasicParsing
if ($LASTEXITCODE -ne 0) { throw "MinGit download failed" }

# 3. 解压到 bin/git-portable/(先清旧内容)
if (Test-Path $GitPortableDir) { Remove-Item -Recurse -Force $GitPortableDir }
New-Item -ItemType Directory -Path $GitPortableDir -Force | Out-Null
Expand-Archive -Path $TempZip -DestinationPath $GitPortableDir -Force
Remove-Item -Force $TempZip

# 4. 验证
$versionOutput = & $GitExe --version 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "git-portable failed verification: $versionOutput"
}

$Size = (Get-Item $GitPortableDir).FullName | ForEach-Object {
    (Get-ChildItem $_ -Recurse | Measure-Object Length -Sum).Sum / 1MB
} | Select-Object -First 1
Write-Host "[ok] Installed git-portable: $versionOutput ($([math]::Round($Size, 1)) MB at $GitPortableDir)" -ForegroundColor Green