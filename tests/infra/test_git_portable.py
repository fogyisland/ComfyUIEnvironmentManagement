"""git_portable resolver 测试。"""
from pathlib import Path
from comfy_mgr.infra.git_portable import (
    default_git_resolver, git_portable_version,
)


def test_default_resolver_prefers_portable(tmp_path: Path):
    portable = tmp_path / "bin" / "git-portable" / "cmd"
    portable.mkdir(parents=True)
    (portable / "git.exe").write_bytes(b"")
    p = default_git_resolver(tmp_path)
    assert p == portable / "git.exe"


def test_default_resolver_falls_back_to_system(tmp_path: Path):
    """无 portable 时返回 shutil.which('git') 结果(可能是 None)。"""
    p = default_git_resolver(tmp_path)
    import shutil
    assert p == shutil.which("git")


def test_git_portable_version_returns_content(tmp_path: Path):
    vf = tmp_path / "bin" / "git-portable"
    vf.mkdir(parents=True)
    (vf / "VERSION").write_text("2.47.0")
    assert git_portable_version(tmp_path) == "2.47.0"


def test_git_portable_version_missing(tmp_path: Path):
    assert git_portable_version(tmp_path) is None
