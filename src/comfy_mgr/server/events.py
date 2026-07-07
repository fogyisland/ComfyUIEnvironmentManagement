"""WSBroadcaster:订阅 in-process bus 'ws.push' 事件,fan-out 到所有 WPF WS client。"""
from __future__ import annotations
import asyncio
import json
import logging
from datetime import datetime
from typing import Any

from fastapi import WebSocket
from comfy_mgr.infra.event_bus import EventBus

logger = logging.getLogger(__name__)


class WSBroadcaster:
    """维护当前所有连接的 WPF WS client。"""

    def __init__(self, bus: EventBus, heartbeat_seconds: float = 30.0) -> None:
        self._clients: set[WebSocket] = set()
        self._lock = asyncio.Lock()
        self._heartbeat = heartbeat_seconds
        self._heartbeat_task: asyncio.Task | None = None
        bus.on("ws.push", self._on_push_sync)

    def add_client(self, ws: WebSocket) -> None:
        self._clients.add(ws)
        logger.info("ws client added, total=%d", len(self._clients))

    async def remove_client(self, ws: WebSocket) -> None:
        async with self._lock:
            self._clients.discard(ws)
        logger.info("ws client removed, remaining=%d", len(self._clients))

    async def disconnect_all(self) -> None:
        async with self._lock:
            for ws in list(self._clients):
                try:
                    await ws.close()
                except Exception:
                    pass
            self._clients.clear()

    def _on_push_sync(self, channel: str, *args: Any) -> None:
        """bus.on 回调(同步);调度 async broadcast。"""
        try:
            loop = asyncio.get_event_loop()
        except RuntimeError:
            return
        if loop.is_running():
            asyncio.create_task(self.broadcast(channel, *args))

    async def broadcast(self, channel: str, *args: Any) -> None:
        msg = {
            "channel": channel,
            "args": list(args),
            "ts": datetime.now().isoformat(timespec="seconds"),
        }
        dead: set[WebSocket] = set()
        async with self._lock:
            clients = list(self._clients)
        for ws in clients:
            try:
                await ws.send_json(msg)
            except Exception as exc:
                logger.warning("ws send failed, marking dead: %s", exc)
                dead.add(ws)
        if dead:
            async with self._lock:
                self._clients -= dead

    async def _heartbeat_loop(self) -> None:
        while True:
            await asyncio.sleep(self._heartbeat)
            await self.broadcast("_ping")

    def start_heartbeat(self) -> None:
        if self._heartbeat_task is None:
            self._heartbeat_task = asyncio.create_task(self._heartbeat_loop())

    async def stop_heartbeat(self) -> None:
        if self._heartbeat_task is not None:
            self._heartbeat_task.cancel()
            try:
                await self._heartbeat_task
            except (asyncio.CancelledError, Exception):
                pass
            self._heartbeat_task = None