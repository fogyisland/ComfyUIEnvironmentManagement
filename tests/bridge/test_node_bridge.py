"""NodeBridge 测试。"""
from unittest.mock import MagicMock
from comfy_mgr.result import Result, ServiceError
from app.bridge.node_bridge import NodeBridge


def test_enableInEnv_emits_nodeEnabled(qapp, qtbot):
    mock_svc = MagicMock()
    mock_svc.enable_in_env.return_value = Result.ok(None)
    bridge = NodeBridge(mock_svc)
    with qtbot.waitSignal(bridge.nodeEnabled, timeout=1000) as blocker:
        result = bridge.enableInEnv("env1", "ltdrdata__ComfyUI-Impact-Pack")
    assert result["ok"]
    assert blocker.args == ["env1", "ltdrdata__ComfyUI-Impact-Pack"]


def test_disableInEnv_emits_nodeDisabled(qapp, qtbot):
    mock_svc = MagicMock()
    mock_svc.disable_in_env.return_value = Result.ok(None)
    bridge = NodeBridge(mock_svc)
    with qtbot.waitSignal(bridge.nodeDisabled, timeout=1000) as blocker:
        result = bridge.disableInEnv("env1", "ltdrdata__ComfyUI-Impact-Pack")
    assert result["ok"]
    assert blocker.args == ["env1", "ltdrdata__ComfyUI-Impact-Pack"]


def test_enableInEnv_emits_error_on_missing(qapp, qtbot):
    mock_svc = MagicMock()
    mock_svc.enable_in_env.return_value = Result.fail(
        ServiceError("ENV_NOT_FOUND", "env1 不存在"))
    bridge = NodeBridge(mock_svc)
    with qtbot.waitSignal(bridge.errorOccurred, timeout=1000) as blocker:
        result = bridge.enableInEnv("env1", "n1")
    assert not result["ok"]
    assert blocker.args == ["ENV_NOT_FOUND", "env1 不存在"]
