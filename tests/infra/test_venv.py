from pathlib import Path
from unittest.mock import MagicMock
from comfy_mgr.infra.venv import VenvManager
from comfy_mgr.result import Result

def test_create_venv_runs_python_m_venv(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.venv.subprocess.run")
    mock_run.return_value = MagicMock(returncode=0, stderr="")
    py = Path("C:/Python310/python.exe")
    venv = Path("D:/envs/env1/venv")
    result = VenvManager.create(py, venv)
    assert result.ok
    args = mock_run.call_args[0][0]
    assert args[0] == str(py)
    assert args[1:3] == ["-m", "venv"]
    assert str(venv) in args

def test_create_venv_returns_fail_on_error(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.venv.subprocess.run")
    mock_run.return_value = MagicMock(returncode=1, stderr="no such file")
    result = VenvManager.create(Path("X"), Path("Y"))
    assert not result.ok
    assert result.error.code == "VENV_CREATE_FAILED"

def test_install_requirements_runs_pip_install(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.venv.subprocess.run")
    mock_run.return_value = MagicMock(returncode=0, stderr="")
    venv_py = Path("D:/envs/env1/venv/Scripts/python.exe")
    req = Path("D:/shared/ComfyUI/requirements.txt")
    result = VenvManager.install_requirements(venv_py, req)
    assert result.ok
    args = mock_run.call_args[0][0]
    assert args[0] == str(venv_py)
    assert args[1:4] == ["-m", "pip", "install"]
    assert "-r" in args
    assert str(req) in args

def test_install_requirements_returns_fail(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.venv.subprocess.run")
    mock_run.return_value = MagicMock(returncode=1, stderr="resolution failed")
    result = VenvManager.install_requirements(Path("X"), Path("Y"))
    assert not result.ok
    assert result.error.code == "VENV_PIP_FAILED"

def test_get_python_version(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.venv.subprocess.run")
    mock_run.return_value = MagicMock(returncode=0, stdout="Python 3.10.5", stderr="")
    result = VenvManager.get_python_version(Path("C:/Python310/python.exe"))
    assert result.ok
    assert result.value == "Python 3.10.5"