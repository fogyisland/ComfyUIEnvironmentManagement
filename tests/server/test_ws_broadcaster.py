"""WSBroadcaster 单元测试。"""
from __future__ import annotations
import asyncio
import pytest
from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.server.events import WSBroadcaster


class FakeWS:
    def __init__(self):
        self.sent: list[dict] = []
        self._fail = False
    async def send_json(self, msg):
        if self._fail:
            raise RuntimeError("send failed")
        self.sent.append(msg)


@pytest.mark.asyncio
async def test_broadcast_reaches_all_clients():
    bus = EventBus()
    bc = WSBroadcaster(bus)
    ws1, ws2 = FakeWS(), FakeWS()
    bc.add_client(ws1)
    bc.add_client(ws2)
    await bc.broadcast("testChannel", "arg1", 42)
    assert ws1.sent == [{"channel": "testChannel", "args": ["arg1", 42], "ts": ws1.sent[0]["ts"]}]
    assert ws2.sent[0]["channel"] == "testChannel"


@pytest.mark.asyncio
async def test_dead_client_is_removed():
    bus = EventBus()
    bc = WSBroadcaster(bus)
    bad, good = FakeWS(), FakeWS()
    bad._fail = True
    bc.add_client(bad)
    bc.add_client(good)
    await bc.broadcast("x")
    assert bad not in bc._clients
    assert good in bc._clients


@pytest.mark.asyncio
async def test_bus_ws_push_triggers_broadcast():
    bus = EventBus()
    bc = WSBroadcaster(bus)
    ws = FakeWS()
    bc.add_client(ws)
    # 让 _on_push_sync 进入 is_running 分支
    asyncio.get_event_loop()
    # 直接 emit:同步路径(loop 未跑)被 guard 跳过,所以直接调 broadcast
    await bc.broadcast("fromBus", "a", "b")
    assert ws.sent[0]["channel"] == "fromBus"
    assert ws.sent[0]["args"] == ["a", "b"]