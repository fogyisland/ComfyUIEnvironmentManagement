"""BaseBridge._invoke 测试。"""
from unittest.mock import MagicMock
from comfy_mgr.result import Result, ServiceError
from app.bridge.base import BaseBridge


def test_invoke_returns_ok_dict_when_service_ok(qapp, qtbot):
    bridge = BaseBridge()
    mock_fn = MagicMock(return_value=Result.ok("hello"))
    result = bridge._invoke(mock_fn, "arg1")
    assert result["ok"] is True
    assert result["value"] == "hello"


def test_invoke_emits_error_signal_on_failure(qapp, qtbot):
    bridge = BaseBridge()
    mock_fn = MagicMock(return_value=Result.fail(
        ServiceError("TEST_CODE", "测试失败")))
    with qtbot.waitSignal(bridge.errorOccurred, timeout=1000) as blocker:
        result = bridge._invoke(mock_fn)
    assert result["ok"] is False
    assert result["error"]["code"] == "TEST_CODE"
    assert "测试失败" in result["error"]["message"]
    assert blocker.args == ["TEST_CODE", "测试失败"]


def test_invoke_passes_args_through(qapp):
    bridge = BaseBridge()
    mock_fn = MagicMock(return_value=Result.ok(None))
    bridge._invoke(mock_fn, "a", 1, 3.14)
    mock_fn.assert_called_once_with("a", 1, 3.14)
