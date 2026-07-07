"""BaseBridge：所有 Bridge 的公共模板（无 PySide6 依赖）。"""
from __future__ import annotations
from typing import Any, Callable
from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.result import Result


class BaseBridge:
    """所有 Bridge 的基类。提供：
    - errorOccurred(code, message) 通过 bus.emit("ws.push", "errorOccurred", code, message)
    - _invoke(fn, *args) 模板：调 Service，Result → dict + 发错误 ws.push
    """

    def __init__(self, bus: EventBus) -> None:
        self.bus = bus

    def _invoke(self, fn: Callable[..., Result], *args: Any) -> dict:
        """统一封装 Service 调用。Result.ok → {ok:True, value};失败 → emit errorOccurred + 返回 envelope。"""
        result = fn(*args)
        if result.ok:
            return {"ok": True, "value": getattr(result, "value", None)}
        code = result.error.code
        message = result.error.message
        self.bus.emit("ws.push", "errorOccurred", code, message)
        return {"ok": False, "error": {"code": code, "message": message}}
