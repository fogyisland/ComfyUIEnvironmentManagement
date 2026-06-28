"""python_exe_resolver:检测 base python 可执行路径。

优先级:
  1. python/python.exe            (项目 portable,M3 主用,Windows)
  2. python/bin/python3            (Linux/Mac 兼容,留 M4+ 跨平台时启用)
  3. shutil.which("python")        (系统兜底)
"""
from __future__ import annotations
import shutil
import sys
from pathlib import Path


def default_python_resolver(project_root: Path) -> "Path | None":
    """默认实现:portable > 系统 python。"""
    if sys.platform == "win32":
        portable = project_root / "python" / "python.exe"
        if portable.exists():
            return portable
    else:
        portable = project_root / "python" / "bin" / "python3"
        if portable.exists():
            return portable
    return shutil.which("python")


def python_portable_version(project_root: Path) -> str | None:
    """读 python/.portable_version,存在则返回内容(用户首次 setup 时写)。"""
    vf = project_root / "python" / ".portable_version"
    if not vf.exists():
        return None
    return vf.read_text(encoding="utf-8").strip()
