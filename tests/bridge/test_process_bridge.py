"""ProcessBridge 测试 — 无 Qt。"""
from __future__ import annotations
from unittest.mock import MagicMock
from datetime import datetime
from pathlib import Path
import pytest
from comfy_mgr.result import Result, ServiceError
from comfy_mgr.models.process import ProcessHandle, ProcessStatus
from comfy_mgr.models.environment import Environment
from app.bridge.process_bridge import ProcessBridge


def _env(env_id="env1"):
    return Environment(
        id=env_id, name=env_id,
        root_path=Path("D:/envs/x"), comfyui_layout="shared",
        comfyui_source=Path("C:/ComfyUI"),
        venv_path=Path("D:/envs/x/v"),
        python_executable=Path("D:/envs/x/v/Scripts/python.exe"),
        custom_nodes_path=Path("D:/envs/x/cn"),
        extra_model_paths_yaml=Path("D:/envs/x/yaml"),
        port=8188,
    )


@pytest.fixture
def bridge(mock_bus):
    mock_svc = MagicMock()
    b = ProcessBridge(service=mock_svc, bus=mock_bus)
    b.set_env_resolver(lambda _id: _env())
    return b, mock_svc


def test_start_env_emits_env_started(bridge, mock_bus):
    b, mock_svc = bridge
    mock_svc.start.return_value = Result.ok(ProcessHandle(
        env_id="env1", pid=4321, port=8188,
        started_at=datetime.now(), log_file=Path("D:/logs/x.log"),
    ))
    result = b.start_env("env1")
    assert result["ok"]
    assert ("ws.push", "envStarted", "env1", 4321, 8188) in mock_bus.emit_calls


def test_stop_env_emits_env_stopped(bridge, mock_bus):
    b, mock_svc = bridge
    mock_svc.stop.return_value = Result.ok(None)
    result = b.stop_env("env1", 5.0)
    assert result["ok"]
    assert ("ws.push", "envStopped", "env1") in mock_bus.emit_calls


def test_get_status_returns_dict(bridge):
    b, mock_svc = bridge
    mock_svc.get_status.return_value = ProcessStatus(
        running=True, pid=555, port=8188,
    )
    result = b.get_status("env1")
    assert result["ok"]
    assert result["value"]["running"] is True
    assert result["value"]["pid"] == 555


def test_on_line_appends_to_logs_and_emits(mock_bus):
    """_on_line 调 bus.emit("ws.push", "logLine", env_id, line)。"""
    bridge = ProcessBridge(service=MagicMock(), bus=mock_bus)
    bridge._on_line("env1", "hello world")
    assert "hello world" in bridge.logs_for("env1")
    assert ("ws.push", "logLine", "env1", "hello world") in mock_bus.emit_calls


def test_start_env_emits_error_on_failure(bridge, mock_bus):
    b, mock_svc = bridge
    mock_svc.start.return_value = Result.fail(
        ServiceError("PROCESS_ALREADY_RUNNING", "已在运行"))
    result = b.start_env("env1")
    assert not result["ok"]
    assert result["error"]["code"] == "PROCESS_ALREADY_RUNNING"
    assert ("ws.push", "errorOccurred", "PROCESS_ALREADY_RUNNING", "已在运行") in mock_bus.emit_calls


def test_logs_for_unknown_env_returns_empty(mock_bus):
    bridge = ProcessBridge(service=MagicMock(), bus=mock_bus)
    assert bridge.logs_for("unknown") == []


def test_log_version_increments_on_each_line(mock_bus):
    """log_version 必须随每次 _on_line 递增。"""
    bridge = ProcessBridge(service=MagicMock(), bus=mock_bus)
    initial = bridge.log_version
    bridge._on_line("env1", "line 1")
    assert bridge.log_version == initial + 1
    bridge._on_line("env1", "line 2")
    assert bridge.log_version == initial + 2