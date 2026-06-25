"""SettingsBridge 桩（M1 T5+ 将实现）。"""
from __future__ import annotations
from app.bridge.base import BaseBridge


class SettingsBridge(BaseBridge):
    """M1 桩：仅占位，T5/T14 将实现 SettingsService 包装。"""

    def __init__(self, service) -> None:
        super().__init__()
        self.service = service
