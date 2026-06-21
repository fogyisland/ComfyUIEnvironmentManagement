import pytest
from pathlib import Path
from unittest.mock import MagicMock
from comfy_mgr.infra.git import GitManager
from comfy_mgr.result import Result

def test_clone_runs_git_clone(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.git.subprocess.run")
    mock_run.return_value = MagicMock(returncode=0, stderr="")
    result = GitManager.clone("https://github.com/x/y", Path("D:/catalog/nodes/y"))
    assert result.ok
    args = mock_run.call_args[0][0]
    assert args[0:2] == ["git", "clone"]
    assert "https://github.com/x/y" in args
    assert str(Path("D:/catalog/nodes/y")) in args

def test_clone_returns_fail_on_error(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.git.subprocess.run")
    mock_run.return_value = MagicMock(returncode=128, stderr="repo not found")
    result = GitManager.clone("https://github.com/x/y", Path("D:/y"))
    assert not result.ok
    assert result.error.code == "GIT_CLONE_FAILED"

def test_clone_returns_fail_on_exception(mocker):
    mocker.patch("comfy_mgr.infra.git.subprocess.run", side_effect=OSError("net down"))
    result = GitManager.clone("https://github.com/x/y", Path("D:/y"))
    assert not result.ok
    assert result.error.code == "GIT_CLONE_FAILED"

def test_pull_runs_git_pull(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.git.subprocess.run")
    mock_run.return_value = MagicMock(returncode=0, stderr="", stdout="Already up to date.")
    result = GitManager.pull(Path("D:/catalog/nodes/y"))
    assert result.ok
    args = mock_run.call_args[0][0]
    assert args[0] == "git"
    assert "pull" in args
    assert str(Path("D:/catalog/nodes/y")) in args

def test_pull_returns_fail_on_conflict(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.git.subprocess.run")
    mock_run.return_value = MagicMock(returncode=1, stderr="CONFLICT")
    result = GitManager.pull(Path("D:/y"))
    assert not result.ok
    assert result.error.code == "GIT_PULL_FAILED"