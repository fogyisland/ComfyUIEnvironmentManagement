import os
import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock
from comfy_mgr.infra.fs import FS
from comfy_mgr.result import Result

# ---- create_junction ----

def test_create_junction_runs_mklink_on_windows(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.fs.subprocess.run")
    mock_run.return_value = MagicMock(returncode=0, stderr="")
    link = Path("C:/envs/env1/ComfyUI")
    target = Path("D:/shared/ComfyUI")
    result = FS.create_junction(link, target)
    assert result.ok
    mock_run.assert_called_once()
    args = mock_run.call_args[0][0]
    assert "mklink" in args
    assert "/J" in args
    assert str(link) in args
    assert str(target) in args

def test_create_junction_returns_fail_on_subprocess_error(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.fs.subprocess.run")
    mock_run.return_value = MagicMock(returncode=1, stderr="Access denied")
    result = FS.create_junction(Path("L"), Path("T"))
    assert not result.ok
    assert result.error.code == "FS_JUNCTION_FAILED"

def test_create_junction_returns_fail_on_exception(mocker):
    mocker.patch("comfy_mgr.infra.fs.subprocess.run", side_effect=OSError("boom"))
    result = FS.create_junction(Path("L"), Path("T"))
    assert not result.ok
    assert result.error.code == "FS_JUNCTION_FAILED"

# ---- remove_junction ----

def test_remove_junction_runs_rmdir(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.fs.subprocess.run")
    mock_run.return_value = MagicMock(returncode=0, stderr="")
    mocker.patch.object(Path, "exists", return_value=True)
    result = FS.remove_junction(Path("C:/envs/env1/ComfyUI"))
    assert result.ok
    mock_run.assert_called_once()
    args = mock_run.call_args[0][0]
    assert "rmdir" in args

def test_remove_junction_succeeds_if_already_gone(tmp_path):
    """目标不存在时也应成功（M0 简化）。"""
    result = FS.remove_junction(tmp_path / "nonexistent")
    assert result.ok

# ---- copy_directory ----

def test_copy_directory_copies_files(tmp_path):
    src = tmp_path / "src"
    src.mkdir()
    (src / "a.txt").write_text("hello")
    (src / "sub").mkdir()
    (src / "sub" / "b.txt").write_text("world")
    dst = tmp_path / "dst"
    result = FS.copy_directory(src, dst)
    assert result.ok
    assert (dst / "a.txt").read_text() == "hello"
    assert (dst / "sub" / "b.txt").read_text() == "world"

# ---- ensure_dir ----

def test_ensure_dir_creates_path(tmp_path):
    target = tmp_path / "a" / "b" / "c"
    result = FS.ensure_dir(target)
    assert result.ok
    assert target.is_dir()

def test_ensure_dir_succeeds_if_exists(tmp_path):
    target = tmp_path / "x"
    target.mkdir()
    result = FS.ensure_dir(target)
    assert result.ok
