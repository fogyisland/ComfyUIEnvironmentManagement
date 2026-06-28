"""从 GitHub release 下载 Git for Windows Portable 到 bin/git-portable/。

用法: poetry run python scripts/fetch_git_portable.py
幂等:目标已存在且 VERSION 匹配则跳过。
"""
from __future__ import annotations
import subprocess
import sys
import urllib.request
import zipfile
from pathlib import Path

GIT_VERSION = "2.47.0"
DOWNLOAD_URL = (
    f"https://github.com/git-for-windows/git/releases/download/"
    f"v{GIT_VERSION}.windows.1/PortableGit-{GIT_VERSION}-64-bit.7z.exe"
)


def fetch(target_root: Path) -> int:
    target = target_root / "bin" / "git-portable"
    version_file = target / "VERSION"

    if version_file.exists() and version_file.read_text().strip() == GIT_VERSION:
        print(f"git portable {GIT_VERSION} 已存在,跳过")
        return 0

    target.mkdir(parents=True, exist_ok=True)
    archive = target / "git-portable.7z.exe"
    print(f"下载 {DOWNLOAD_URL} → {archive}")
    urllib.request.urlretrieve(DOWNLOAD_URL, archive)

    print(f"解压到 {target}(自解压 .exe,需 7z 或 ./git-portable.7z.exe -y)")

    # 自解压:PortableGit-*.7z.exe 是 7z SFX,直接执行 -y 解压到当前目录
    subprocess.run(
        [str(archive), "-y", f"-o{target}"],
        check=True, timeout=300,
    )

    # 清理 .exe 安装器
    archive.unlink(missing_ok=True)
    version_file.write_text(GIT_VERSION, encoding="utf-8")
    print(f"git portable {GIT_VERSION} 安装完成")
    return 0


if __name__ == "__main__":
    sys.exit(fetch(Path(__file__).parent.parent))
