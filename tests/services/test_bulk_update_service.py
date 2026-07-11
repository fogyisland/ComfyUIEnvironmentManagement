"""M5 T1: BulkUpdateService 单测。"""
from __future__ import annotations
import asyncio
import pytest
from unittest.mock import MagicMock, AsyncMock
from comfy_mgr.result import Result, ServiceError
from comfy_mgr.services.bulk_update_service import BulkUpdateService


def _ok_envelope(value=None):
    """upgrade_node 实际返回 dict envelope(Fix 1: bridge dict contract)。"""
    return {"ok": True, "value": value or {"version": "v1.2"}}


def _fail_envelope(code, message):
    return {"ok": False, "error": {"code": code, "message": message}}


@pytest.fixture
def fake_node_bridge():
    bridge = MagicMock()
    bridge.upgrade_node = MagicMock(return_value=_ok_envelope())
    return bridge


@pytest.fixture
def fake_bus():
    bus = MagicMock()
    bus.emit = MagicMock()
    return bus


def test_start_returns_bulk_id(fake_node_bridge, fake_bus):
    svc = BulkUpdateService(fake_node_bridge, fake_bus)
    r = svc.start(env_ids=["env-1"], node_ids=["node-a", "node-b"])
    assert r.ok
    assert isinstance(r.value, str)
    assert len(r.value) == 36  # UUID4 长度


def test_start_validates_empty_env_ids(fake_node_bridge, fake_bus):
    svc = BulkUpdateService(fake_node_bridge, fake_bus)
    r = svc.start(env_ids=[], node_ids=["node-a"])
    assert not r.ok
    assert r.error.code == "BAD_VALIDATION"


def test_start_validates_empty_node_ids(fake_node_bridge, fake_bus):
    svc = BulkUpdateService(fake_node_bridge, fake_bus)
    r = svc.start(env_ids=["env-1"], node_ids=[])
    assert not r.ok
    assert r.error.code == "BAD_VALIDATION"


def test_get_status_returns_pending_immediately(fake_node_bridge, fake_bus):
    svc = BulkUpdateService(fake_node_bridge, fake_bus)
    r = svc.start(env_ids=["env-1"], node_ids=["node-a", "node-b"])
    bulk_id = r.value
    s = svc.get_status(bulk_id)
    assert s.ok
    assert s.value["status"] == "pending"
    assert s.value["total"] == 2  # 1 env × 2 nodes


def test_cancel_unknown_id_returns_bulk_not_found(fake_node_bridge, fake_bus):
    svc = BulkUpdateService(fake_node_bridge, fake_bus)
    r = svc.cancel("nonexistent")
    assert not r.ok
    assert r.error.code == "BULK_NOT_FOUND"


def test_get_status_unknown_returns_bulk_not_found(fake_node_bridge, fake_bus):
    svc = BulkUpdateService(fake_node_bridge, fake_bus)
    r = svc.get_status("nonexistent")
    assert not r.ok
    assert r.error.code == "BULK_NOT_FOUND"


def test_cancel_records_checkpoint(fake_node_bridge, fake_bus):
    """cancel 后 status 应记录 cancelled_at_checkpoint。"""
    svc = BulkUpdateService(fake_node_bridge, fake_bus)
    r = svc.start(env_ids=["env-1"], node_ids=["node-a", "node-b"])
    bulk_id = r.value
    r2 = svc.cancel(bulk_id)
    assert r2.ok
    # get_status 应能查到 checkpoint
    s = svc.get_status(bulk_id)
    assert s.ok
    assert s.value["cancelled_at_checkpoint"] is not None


def test_run_bulk_marks_version_locked_as_skipped():
    """Fix 2: VERSION_LOCKED 应当映射成 skipped(Fix 2)。"""
    bridge = MagicMock()
    bridge.upgrade_node = MagicMock(return_value=_fail_envelope(
        "VERSION_LOCKED", "节点已锁定,无法升级"))
    bus = MagicMock()
    svc = BulkUpdateService(bridge, bus)
    r = svc.start(env_ids=["env-1"], node_ids=["node-a"])
    assert r.ok
    bulk_id = r.value
    # 手动驱动后台 task:测试环境无 running loop,start() 不会真正调度
    # 用 _run_bulk 直接同步跑一次
    import asyncio
    rec = svc._bulks[bulk_id]
    # 同步执行 _run_bulk(它接受已运行的 loop;测试环境没就手动跑)
    loop = asyncio.new_event_loop()
    try:
        loop.run_until_complete(svc._run_bulk(rec))
    finally:
        loop.close()
    # VERSION_LOCKED 应被算作 skipped
    assert rec.skipped == 1
    assert rec.failed == 0
