"""ProcessBridge 桩（M1 T8+ 将实现 QProcess 转发）。

T4 需要这个桩暴露：
- _on_line(line: str)  — process.bridge_sink 回调入口
- set_env_resolver(callable) — T4 在 AppContext 中注入 env_id → Environment 解析器
"""
from __future__ import annotations
from typing import Callable, Optional
from app.bridge.base import BaseBridge


class ProcessBridge(BaseBridge):
    """M1 桩：仅占位，T8 将实现 QProcess 集成。

    T4 需要最小钩子以便 AppContext 能连线。
    """

    def __init__(self, process_service) -> None:
        super().__init__()
        self.process_service = process_service
        self._env_resolver: Optional[Callable[[str], object]] = None

    def _on_line(self, line: str) -> None:  # noqa: D401 - M1 stub
        """M0/T4 桩：吞掉行；T8 将 emit Signal。"""
        return None

    def set_env_resolver(self, resolver: Callable[[str], object]) -> None:
        """注入 env_id → Environment 解析器（T4 在 AppContext 中调用）。"""
        self._env_resolver = resolver
