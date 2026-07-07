"""BaseBridge._invoke 测试 — 无 Qt。"""
from unittest.mock import MagicMock
from comfy_mgr.result import Result, ServiceError
from app.bridge.base import BaseBridge


def test_invoke_returns_ok_dict_when_service_ok(mock_bus):
    bridge = BaseBridge(bus=mock_bus)
    mock_fn = MagicMock(return_value=Result.ok("hello"))
    result = bridge._invoke(mock_fn, "arg1")
    assert result["ok"] is True
    assert result["value"] == "hello"


def test_invoke_emits_error_on_failure(mock_bus):
    bridge = BaseBridge(bus=mock_bus)
    mock_fn = MagicMock(return_value=Result.fail(
        ServiceError("TEST_CODE", "测试失败")))
    result = bridge._invoke(mock_fn)
    assert result["ok"] is False
    assert result["error"]["code"] == "TEST_CODE"
    assert "测试失败" in result["error"]["message"]
    assert ("ws.push", "errorOccurred", "TEST_CODE", "测试失败") in mock_bus.emit_calls


def test_invoke_passes_args_through(mock_bus):
    bridge = BaseBridge(bus=mock_bus)
    mock_fn = MagicMock(return_value=Result.ok(None))
    bridge._invoke(mock_fn, "a", 1, 3.14)
    mock_fn.assert_called_once_with("a", 1, 3.14)