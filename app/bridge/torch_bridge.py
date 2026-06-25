"""TorchBridge 桩（M1 T14+ 将实现）。"""
from __future__ import annotations
from app.bridge.base import BaseBridge


class TorchBridge(BaseBridge):
    """M1 桩：仅占位，T14 将实现 CudaDetector + PyTorchInstaller 包装。"""

    def __init__(self, torch_helper) -> None:
        super().__init__()
        self.torch_helper = torch_helper
