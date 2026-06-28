"""git_exe_resolver:检测 git 可执行路径。

优先级:
  1. bin/git-portable/cmd/git.exe  (项目 portable,M3 主用)
  2. shutil.which("git")            (系统 PATH 兜底)
  3. None                           (找不到)
"""
from __future__ import annotations
import shutil
from pathlib import Path


def default_git_resolver(project_root: Path) -> "Path | None":
    """默认实现:portable > 系统 git。"""
    portable = project_root / "bin" / "git-portable" / "cmd" / "git.exe"
    if portable.exists():
        return portable
    return shutil.which("git")


def git_portable_version(project_root: Path) -> str | None:
    """读 bin/git-portable/VERSION,存在则返回内容,否则 None。"""
    vf = project_root / "bin" / "git-portable" / "VERSION"
    if not vf.exists():
        return None
    return vf.read_text(encoding="utf-8").strip()
