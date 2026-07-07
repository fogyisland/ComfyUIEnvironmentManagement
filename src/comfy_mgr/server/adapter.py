"""call_slot:统一把 bridge method 调用结果转成 envelope。"""
from __future__ import annotations
import logging
from typing import Any, Callable

logger = logging.getLogger(__name__)


def call_slot(bridge: Any, method_name: str, **kwargs: Any) -> dict:
    """调 bridge.<method_name>(**kwargs) 并把异常转成 envelope。

    Bridge method 返回 dict {ok, value|error} 时直接透传;
    抛任何异常时返回 {ok: false, error: {code: INTERNAL, message: str(exc)}}。
    """
    try:
        method = getattr(bridge, method_name)
        if not callable(method):
            return {"ok": False, "error": {
                "code": "INTERNAL",
                "message": f"{method_name} 不是可调用方法",
            }}
        result = method(**kwargs)
        if isinstance(result, dict) and "ok" in result:
            return result
        return {"ok": True, "value": result}
    except Exception as exc:
        logger.exception("call_slot %s failed", method_name)
        return {"ok": False, "error": {
            "code": "INTERNAL", "message": str(exc),
        }}