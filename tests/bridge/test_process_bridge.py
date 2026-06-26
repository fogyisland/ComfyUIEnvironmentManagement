"""ProcessBridge 测试。"""
from unittest.mock import MagicMock
from comfy_mgr.result import Result, ServiceError
from comfy_mgr.models.process import ProcessHandle, ProcessStatus
from datetime import datetime
from pathlib import Path
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


def test_startEnv_emits_processStarted(qapp, qtbot):
    mock_svc = MagicMock()
    mock_svc.start.return_value = Result.ok(ProcessHandle(
        env_id="env1", pid=4321, port=8188,
        started_at=datetime.now(), log_file=Path("D:/logs/x.log"),
    ))
    bridge = ProcessBridge(mock_svc)
    bridge.set_env_resolver(lambda _id: _env())
    with qtbot.waitSignal(bridge.processStarted, timeout=1000) as blocker:
        result = bridge.startEnv("env1")
    assert result["ok"]
    assert blocker.args == ["env1", 4321, 8188]


def test_stopEnv_emits_processStopped(qapp, qtbot):
    mock_svc = MagicMock()
    mock_svc.stop.return_value = Result.ok(None)
    bridge = ProcessBridge(mock_svc)
    bridge.set_env_resolver(lambda _id: _env())
    with qtbot.waitSignal(bridge.processStopped, timeout=1000) as blocker:
        result = bridge.stopEnv("env1", 5.0)
    assert result["ok"]
    assert blocker.args == ["env1"]


def test_getStatus_returns_dict(qapp):
    mock_svc = MagicMock()
    mock_svc.get_status.return_value = ProcessStatus(
        running=True, pid=555, port=8188,
    )
    bridge = ProcessBridge(mock_svc)
    bridge.set_env_resolver(lambda _id: _env())
    result = bridge.getStatus("env1")
    assert result["ok"]
    assert result["value"]["running"] is True
    assert result["value"]["pid"] == 555


def test_on_line_appends_to_logs_and_emits(qapp, qtbot):
    mock_svc = MagicMock()
    bridge = ProcessBridge(mock_svc)
    with qtbot.waitSignal(bridge.processLogLine, timeout=1000) as blocker:
        bridge._on_line("env1", "hello world")
    assert "hello world" in bridge.logsFor("env1")
    assert blocker.args == ["env1", "hello world"]


def test_startEnv_emits_error_on_failure(qapp, qtbot):
    mock_svc = MagicMock()
    mock_svc.start.return_value = Result.fail(
        ServiceError("PROCESS_ALREADY_RUNNING", "已在运行"))
    bridge = ProcessBridge(mock_svc)
    bridge.set_env_resolver(lambda _id: _env())
    with qtbot.waitSignal(bridge.errorOccurred, timeout=1000) as blocker:
        result = bridge.startEnv("env1")
    assert not result["ok"]
    assert result["error"]["code"] == "PROCESS_ALREADY_RUNNING"
    assert blocker.args == ["PROCESS_ALREADY_RUNNING", "已在运行"]


def test_logsFor_unknown_env_returns_empty(qapp):
    bridge = ProcessBridge(MagicMock())
    assert bridge.logsFor("unknown") == []


def test_logVersion_increments_on_each_line(qapp, qtbot):
    """logVersion 必须随每次 _on_line 递增，让 QML binding 重算 logsFor()。"""
    mock_svc = MagicMock()
    bridge = ProcessBridge(mock_svc)
    initial = bridge.logVersion
    bridge._on_line("env1", "line 1")
    assert bridge.logVersion == initial + 1
    bridge._on_line("env1", "line 2")
    assert bridge.logVersion == initial + 2
