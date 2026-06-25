"""TorchBridge 测试。"""
from unittest.mock import MagicMock
from comfy_mgr.result import Result, ServiceError
from app.bridge.torch_bridge import TorchBridge


def test_detectCuda_emits_cudaDetected_and_returns(qapp, qtbot):
    cuda = MagicMock()
    cuda.detect.return_value = Result.ok({
        "available": True, "driver": "552.22", "gpus": ["RTX 4090"],
        "supported_cu": ["cu121", "cu124"],
    })
    bridge = TorchBridge(cuda=cuda)
    with qtbot.waitSignal(bridge.cudaDetected, timeout=1000) as blocker:
        result = bridge.detectCuda()
    assert result["ok"]
    assert result["value"]["available"] is True
    assert blocker.args[0]["driver"] == "552.22"


def test_detectCuda_emits_error_on_failure(qapp, qtbot):
    cuda = MagicMock()
    cuda.detect.return_value = Result.fail(
        ServiceError("CUDA_DETECT_FAILED", "nvidia-smi 不可用"))
    bridge = TorchBridge(cuda=cuda)
    with qtbot.waitSignal(bridge.errorOccurred, timeout=1000) as blocker:
        result = bridge.detectCuda()
    assert not result["ok"]
    assert blocker.args == ["CUDA_DETECT_FAILED", "nvidia-smi 不可用"]


def test_suggestedCuVersions_lists_all(qapp):
    bridge = TorchBridge(cuda=MagicMock())
    versions = bridge.suggestedCuVersions
    assert "cu124" in versions
    assert "cpu" in versions


def test_initEnvTorch_no_environment_returns_error(qapp):
    bridge = TorchBridge(cuda=MagicMock())
    result = bridge.initEnvTorch("env1", "cu124")
    assert not result["ok"]
    assert result["error"]["code"] == "TORCH_INIT_FAILED"