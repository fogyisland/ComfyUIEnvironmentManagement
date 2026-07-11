"""M5 T1: BulkUpdateService 单测。"""
from __future__ import annotations
import asyncio
import pytest
from unittest.mock import MagicMock, AsyncMock
from comfy_mgr.result import Result, ServiceError
from comfy_mgr.services.bulk_update_service import BulkUpdateService


def _ok(value=None):
    return Result.ok(value)


def _fail(code, message):
    return Result.fail(ServiceError(code=code, message=message))


@pytest.fixture
def fake_node_bridge():
    bridge = MagicMock()
    bridge.upgrade_node = MagicMock(return_value=_ok({"version": "v1.2"}))
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