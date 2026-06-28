"""python_portable resolver 测试。"""
import sys
from pathlib import Path
from comfy_mgr.infra.python_portable import (
    default_python_resolver, python_portable_version,
)


def test_resolver_prefers_portable_win(tmp_path: Path):
    if sys.platform != "win32":
        return  # 只在 Windows 跑
    portable = tmp_path / "python" / "python.exe"
    portable.parent.mkdir(parents=True)
    portable.write_bytes(b"")
    assert default_python_resolver(tmp_path) == portable


def test_resolver_falls_back_to_system(tmp_path: Path):
    import shutil
    assert default_python_resolver(tmp_path) == shutil.which("python")


def test_version_returns_content(tmp_path: Path):
    pf = tmp_path / "python" / ".portable_version"
    pf.parent.mkdir(parents=True)
    pf.write_text("3.10.6")
    assert python_portable_version(tmp_path) == "3.10.6"


def test_version_missing(tmp_path: Path):
    assert python_portable_version(tmp_path) is None
