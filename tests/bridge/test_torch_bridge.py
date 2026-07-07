"""TorchBridge 测试 — 无 Qt。"""
from __future__ import annotations
from unittest.mock import MagicMock
import pytest
from comfy_mgr.result import Result, ServiceError
from app.bridge.torch_bridge import TorchBridge


@pytest.fixture
def bridge(mock_bus):
    cuda = MagicMock()
    b = TorchBridge(bus=mock_bus, cuda=cuda, pytorch=MagicMock(), environment=None)
    return b, cuda


def test_detect_cuda_emits_cuda_detected_and_returns(bridge, mock_bus):
    b, cuda = bridge
    cuda.detect.return_value = Result.ok({
        "available": True, "driver": "552.22", "gpus": ["RTX 4090"],
        "supported_cu": ["cu121", "cu124"],
    })
    result = b.detect_cuda()
    assert result["ok"]
    assert result["value"]["available"] is True
    # bus.emit("ws.push", "cudaDetected", dict)
    found = False
    for call in mock_bus.emit_calls:
        if call[0] == "ws.push" and call[1] == "cudaDetected" and call[2]["driver"] == "552.22":
            found = True
            break
    assert found, f"expected ('ws.push', 'cudaDetected', {{'driver': '552.22', ...}}) in {mock_bus.emit_calls}"


def test_detect_cuda_emits_error_on_failure(bridge, mock_bus):
    b, cuda = bridge
    cuda.detect.return_value = Result.fail(
        ServiceError("CUDA_DETECT_FAILED", "nvidia-smi 不可用"))
    result = b.detect_cuda()
    assert not result["ok"]
    assert ("ws.push", "errorOccurred", "CUDA_DETECT_FAILED", "nvidia-smi 不可用") in mock_bus.emit_calls


def test_suggested_cu_versions_lists_all(bridge):
    b, _ = bridge
    versions = b.suggested_cu_versions
    assert "cu124" in versions
    assert "cpu" in versions


def test_init_env_torch_no_environment_returns_error(bridge):
    b, _ = bridge
    result = b.init_env_torch("env1", "cu124")
    assert not result["ok"]
    assert result["error"]["code"] == "TORCH_INIT_FAILED"