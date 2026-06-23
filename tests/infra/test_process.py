import time
import sys
import subprocess
from pathlib import Path
from unittest.mock import MagicMock, patch
from comfy_mgr.infra.process import ProcessService, ProcessHandle, ProcessStatus
from comfy_mgr.models.environment import Environment
from comfy_mgr.result import Result


def make_env(**overrides):
    defaults = dict(
        id="env1",
        name="env1",
        root_path=Path("D:/envs/env1"),
        comfyui_layout="shared",
        comfyui_source=Path("D:/shared/ComfyUI"),
        venv_path=Path("D:/envs/env1/venv"),
        python_executable=Path("D:/envs/env1/venv/Scripts/python.exe"),
        custom_nodes_path=Path("D:/envs/env1/custom_nodes"),
        extra_model_paths_yaml=Path("D:/envs/env1/extra_model_paths.yaml"),
        port=8188,
    )
    defaults.update(overrides)
    return Environment(**defaults)


def test_start_spawns_python_with_args(mocker, tmp_path):
    mock_popen = mocker.patch("comfy_mgr.infra.process.subprocess.Popen")
    mock_popen.return_value = MagicMock(pid=1234)

    svc = ProcessService(log_dir=tmp_path)
    env = make_env()
    result = svc.start(env)
    assert result.ok
    assert result.value.pid == 1234
    assert result.value.port == 8188

    args = mock_popen.call_args[0][0]
    assert str(env.python_executable) in args
    assert "main.py" in " ".join(args)
    assert "--port" in args
    assert "8188" in args


def test_start_returns_fail_if_popen_raises(mocker, tmp_path):
    mocker.patch("comfy_mgr.infra.process.subprocess.Popen", side_effect=OSError("boom"))
    svc = ProcessService(log_dir=tmp_path)
    result = svc.start(make_env())
    assert not result.ok
    assert result.error.code == "PROCESS_START_FAILED"


def test_stop_terminates_process(mocker, tmp_path):
    mock_proc = MagicMock()
    mock_proc.pid = 9999
    mock_proc.wait.return_value = 0
    svc = ProcessService(log_dir=tmp_path)
    # 先 start
    mocker.patch("comfy_mgr.infra.process.subprocess.Popen", return_value=mock_proc)
    env = make_env()
    svc.start(env)
    # stop
    result = svc.stop(env)
    assert result.ok
    mock_proc.terminate.assert_called_once()


def test_stop_kills_on_timeout(mocker, tmp_path):
    mock_proc = MagicMock()
    mock_proc.pid = 9999
    mock_proc.wait.side_effect = [subprocess.TimeoutExpired(cmd="x", timeout=5), 0]
    svc = ProcessService(log_dir=tmp_path)
    mocker.patch("comfy_mgr.infra.process.subprocess.Popen", return_value=mock_proc)
    env = make_env()
    svc.start(env)
    result = svc.stop(env, timeout=5.0)
    assert result.ok
    mock_proc.kill.assert_called_once()


def test_get_status_running(mocker, tmp_path):
    mock_proc = MagicMock()
    mock_proc.pid = 9999
    mock_proc.poll.return_value = None
    svc = ProcessService(log_dir=tmp_path)
    mocker.patch("comfy_mgr.infra.process.subprocess.Popen", return_value=mock_proc)
    env = make_env()
    svc.start(env)
    status = svc.get_status(env)
    assert status.running is True
    assert status.pid == 9999


def test_get_status_stopped(mocker, tmp_path):
    mock_proc = MagicMock()
    mock_proc.pid = 9999
    mock_proc.poll.return_value = 0  # 已退出
    svc = ProcessService(log_dir=tmp_path)
    mocker.patch("comfy_mgr.infra.process.subprocess.Popen", return_value=mock_proc)
    env = make_env()
    svc.start(env)
    status = svc.get_status(env)
    assert status.running is False
