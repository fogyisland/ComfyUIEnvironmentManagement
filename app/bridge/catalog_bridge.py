"""CatalogBridge 桩（M1 T6+ 将实现）。"""
from __future__ import annotations
from app.bridge.base import BaseBridge


class CatalogBridge(BaseBridge):
    """M1 桩：仅占位，T6 将实现 CatalogService 包装。"""

    def __init__(self, service) -> None:
        super().__init__()
        self.service = service
