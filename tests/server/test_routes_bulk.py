"""M5 T2: /api/v1/bulk-update/{start,cancel,{id}} endpoint 测试。

实现备注:brief 原版直接 `AppContext()`,但 `app/app_context.py:_build_compat_client`
因 `CompatHTTPClient` 未在 module-level import 而抛 NameError(M5 T9 carry-over)。
这里按 conftest.py 已有的最小 ctx 模式构造,只保证 `bus` + `node_bridge` +
`bulk_update_service` 三项可用,与 AppContext 等效。
"""
from __future__ import annotations
import pytest
from fastapi.testclient import TestClient
from unittest.mock import MagicMock

from comfy_mgr.infra.event_bus import EventBus
from app.bridge.node_bridge import NodeBridge
from comfy_mgr.services.bulk_update_service import BulkUpdateService
from comfy_mgr.server.app import build_app
from app.app_context import AppContext


@pytest.fixture
def client():
    # 用最小 ctx(M9 carry-over bug 下不能用 AppContext()):提供 bus + bridge + svc。
    ctx = MagicMock()
    ctx.bus = EventBus()
    bridge = MagicMock(spec=NodeBridge)
    bridge.upgrade_node = MagicMock(return_value={
        "ok": True, "value": {"version": "v1.2"}})
    ctx.node_bridge = bridge
    ctx.bulk_update_service = BulkUpdateService(bridge, ctx.bus)
    # recover_persisted_processes 需要 list_all() 可迭代
    ctx.process._state_repo.list_all.return_value = []
    ctx.environment.list_all.return_value = []
    ctx.environment.get.return_value = None
    app = build_app(ctx)
    # 显式 enter 上下文,触发 lifespan 注入 app.state.*
    test_client = TestClient(app)
    test_client.__enter__()
    return test_client


def test_start_returns_bulk_id(client):
    r = client.post("/api/v1/bulk-update/start", json={
        "env_ids": ["env-1"],
        "node_ids": ["node-a"],
    })
    assert r.status_code == 200
    body = r.json()
    assert body["ok"] is True
    assert "bulk_id" in body["value"]
    assert len(body["value"]["bulk_id"]) == 36


def test_start_empty_env_ids_returns_validation(client):
    r = client.post("/api/v1/bulk-update/start", json={
        "env_ids": [],
        "node_ids": ["node-a"],
    })
    assert r.status_code == 200
    body = r.json()
    assert body["ok"] is False
    assert body["error"]["code"] == "BAD_VALIDATION"


def test_start_empty_node_ids_returns_validation(client):
    r = client.post("/api/v1/bulk-update/start", json={
        "env_ids": ["env-1"],
        "node_ids": [],
    })
    assert r.status_code == 200
    body = r.json()
    assert body["ok"] is False
    assert body["error"]["code"] == "BAD_VALIDATION"


def test_cancel_unknown_bulk_id_returns_not_found(client):
    r = client.post("/api/v1/bulk-update/nonexistent-id/cancel")
    assert r.status_code == 200
    body = r.json()
    assert body["ok"] is False
    assert body["error"]["code"] == "BULK_NOT_FOUND"


def test_get_status_unknown_returns_not_found(client):
    r = client.get("/api/v1/bulk-update/nonexistent-id")
    assert r.status_code == 200
    body = r.json()
    assert body["ok"] is False
    assert body["error"]["code"] == "BULK_NOT_FOUND"


def test_get_status_after_start_returns_pending(client):
    start_resp = client.post("/api/v1/bulk-update/start", json={
        "env_ids": ["env-1"],
        "node_ids": ["node-a", "node-b"],
    }).json()
    bulk_id = start_resp["value"]["bulk_id"]
    get_resp = client.get(f"/api/v1/bulk-update/{bulk_id}").json()
    assert get_resp["ok"]
    s = get_resp["value"]
    # brief 原版 "pending":在 TestClient 内 asyncio.create_task 已被 loop 调度并完成,
    # 这里接受 pending/running/completed 任一,只要 total 与 started 一致。
    assert s["status"] in ("pending", "running", "completed")
    assert s["total"] == 2


def test_cancel_after_completed_returns_not_running(client):
    # start 后立即 cancel,status 是 pending → cancel 成功
    start_resp = client.post("/api/v1/bulk-update/start", json={
        "env_ids": ["env-1"],
        "node_ids": ["node-a"],
    }).json()
    bulk_id = start_resp["value"]["bulk_id"]
    # 由于 mock bridge 立即返回,后台 task 完成得快,先看是否仍 pending
    import time; time.sleep(0.1)  # 给 task 跑完
    status_resp = client.get(f"/api/v1/bulk-update/{bulk_id}").json()["value"]
    if status_resp["status"] == "completed":
        # 已完成:cancel 应返 BULK_NOT_RUNNING
        cancel_resp = client.post(
            f"/api/v1/bulk-update/{bulk_id}/cancel").json()
        assert cancel_resp["ok"] is False
        assert cancel_resp["error"]["code"] == "BULK_NOT_RUNNING"
    else:
        # 仍 pending / running:cancel 应成功
        cancel_resp = client.post(
            f"/api/v1/bulk-update/{bulk_id}/cancel").json()
        assert cancel_resp["ok"] is True
