"""BaseBridge：所有 Bridge 的公共模板。"""
from __future__ import annotations
from typing import Any, Callable
from PySide6.QtCore import QObject, Signal
from comfy_mgr.result import Result


class BaseBridge(QObject):
    """所有 Bridge 的基类。提供：
    - errorOccurred(code, message) 全局错误总线 Signal
    - _invoke(fn, *args) 模板：调 Service，Result → dict + 发错误 Signal
    """

    errorOccurred = Signal(str, str)  # code, message

    def _invoke(self, fn: Callable[..., Result], *args: Any) -> dict:
        """统一封装 Service 调用。"""
        result = fn(*args)
        if result.ok:
            return {"ok": True, "value": getattr(result, "value", None)}
        msg = self._tr(result.error.message)
        self.errorOccurred.emit(result.error.code, msg)
        return {"ok": False, "error": {"code": result.error.code, "message": msg}}

    def _tr(self, message: str) -> str:
        """翻译消息（QObject 子类可用 self.tr()）。"""
        try:
            return self.tr(message)
        except Exception:
            return message
