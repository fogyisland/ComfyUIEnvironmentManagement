"""shared/errors.json 加载 + severity/message 查询。"""
from __future__ import annotations
import json
import threading
from pathlib import Path
from typing import TypedDict

_LOCK = threading.Lock()
_CACHE: dict | None = None


class ErrorInfo(TypedDict):
    http_status: int
    severity: str
    i18n_zh_CN: str
    i18n_en_US: str


def _errors_path() -> Path:
    return Path(__file__).parent.parent.parent.parent / "shared" / "errors.json"


def load_errors() -> dict:
    global _CACHE
    with _LOCK:
        if _CACHE is None:
            with _errors_path().open(encoding="utf-8") as f:
                _CACHE = json.load(f)
        return _CACHE


def get_error(code: str) -> ErrorInfo | None:
    """查 error code 定义;未找到返回 None。"""
    data = load_errors()
    info = data.get(code)
    if info is None or code.startswith("_"):
        return None
    return ErrorInfo(
        http_status=info["http_status"],
        severity=info["severity"],
        i18n_zh_CN=info["i18n_zh_CN"],
        i18n_en_US=info["i18n_en_US"],
    )


def classify_severity(code: str) -> str:
    info = get_error(code)
    return info["severity"] if info else "warn"


def format_message(code: str, locale: str = "en_US", **params) -> str:
    info = get_error(code)
    if info is None:
        return code
    template = info.get(f"i18n_{locale}", info["i18n_en_US"])
    try:
        return template.format(**params)
    except (KeyError, IndexError):
        return template
