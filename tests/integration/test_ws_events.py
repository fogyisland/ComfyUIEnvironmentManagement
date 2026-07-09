"""WebSocket 集成测试。"""
from __future__ import annotations
import asyncio
import pytest
from fastapi.testclient import TestClient


def test_ws_connect_receives_emit(app_with_client):
    app, client = app_with_client
    with client.websocket_connect("/ws/events") as ws:
        app.state.bus.emit("ws.push", "versionChanged", "env-1", "test-pkg")
        # bus.on 同步回调 → run_coroutine_threadsafe 跨线程 dispatch,
        # 留出时间让 lifespan 那个 event loop 跑完 broadcast。
        import time; time.sleep(0.2)
        msg = ws.receive_json()
        # 收到 ws.push 之前可能先收到心跳(只在 >30s 后才发,这里跳过)
        if msg.get("channel") == "_ping":
            msg = ws.receive_json()
        assert msg["channel"] == "versionChanged"
        assert msg["args"] == ["env-1", "test-pkg"]
        assert "ts" in msg


def test_ws_multiple_clients_both_receive(app_with_client):
    app, client = app_with_client
    with client.websocket_connect("/ws/events") as ws1, \
         client.websocket_connect("/ws/events") as ws2:
        app.state.bus.emit("ws.push", "settingsChanged", "language")
        import time; time.sleep(0.2)
        m1 = ws1.receive_json()
        m2 = ws2.receive_json()
        if m1.get("channel") == "_ping":
            m1 = ws1.receive_json()
        if m2.get("channel") == "_ping":
            m2 = ws2.receive_json()
        assert m1["channel"] == "settingsChanged"
        assert m2["channel"] == "settingsChanged"


def test_ws_disconnect_removes_client(app_with_client):
    app, client = app_with_client
    with client.websocket_connect("/ws/events") as ws:
        assert len(app.state.ws_broadcaster._clients) == 1
    # disconnect handler 在 lifespan 线程 finally 异步 remove_client,
    # 留出时间让它跑完
    import time; time.sleep(0.3)
    assert len(app.state.ws_broadcaster._clients) == 0


def test_ws_serialize_failed_does_not_close(app_with_client):
    """emit 一个含无法 JSON 序列化的对象 → WS 不应关闭。"""
    app, client = app_with_client
    with client.websocket_connect("/ws/events") as ws:
        # emit 一个 set,set 默认不可 JSON 序列化
        # bus.emit 自身可能因为 _on_push_sync 抛错而吞掉
        try:
            app.state.bus.emit("ws.push", "test", {1, 2, 3})
        except TypeError:
            pass  # bus.emit 自身可能抛
        # WS 仍可收到下一条
        app.state.bus.emit("ws.push", "settingsChanged")
        import time; time.sleep(0.2)
        msg = ws.receive_json()
        if msg.get("channel") == "_ping":
            msg = ws.receive_json()
        assert msg["channel"] == "settingsChanged"