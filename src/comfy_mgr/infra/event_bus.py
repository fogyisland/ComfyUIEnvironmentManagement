"""EventBus:进程内轻量事件总线,同步 emit,handler 异常被吞。"""
from __future__ import annotations
import logging
from typing import Callable

logger = logging.getLogger(__name__)


class EventBus:
    """同步 emit,无队列,无异步语义。"""

    def __init__(self) -> None:
        self._handlers: dict[str, list[Callable]] = {}

    def on(self, event: str, handler: Callable) -> None:
        self._handlers.setdefault(event, []).append(handler)

    def off(self, event: str, handler: Callable) -> None:
        if event in self._handlers and handler in self._handlers[event]:
            self._handlers[event].remove(handler)

    def emit(self, event: str, *args, **kwargs) -> None:
        for h in list(self._handlers.get(event, [])):
            try:
                h(*args, **kwargs)
            except Exception as e:
                logger.exception("EventBus handler for %r raised: %s", event, e)
