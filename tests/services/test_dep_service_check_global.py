"""M4 T17: check_global 失败降级 + base_url 缺省跳过。"""
from __future__ import annotations
import pytest
from unittest.mock import MagicMock
from comfy_mgr.services.dependency import DepService
from comfy_mgr.infra.compat_http_client import CompatHTTPClient
from comfy_mgr.result import Result, ServiceError


def _make_service(compat_client):
    from comfy_mgr.db.dep_repo import DepRepo
    from comfy_mgr.db.scanned_node_repo import ScannedNodeRepo
    from comfy_mgr.infra.event_bus import EventBus
    bus = EventBus()
    return DepService(
        dep_repo=DepRepo(MagicMock()),
        scanned_repo=ScannedNodeRepo(MagicMock()),
        conn=MagicMock(),
        bus=bus,
        compat_client=compat_client,
    )


def test_check_global_no_client_returns_empty():
    svc = _make_service(compat_client=None)
    r = svc.check_global("env-1")
    assert r.ok and r.value == []


def test_check_global_empty_base_url_returns_empty():
    """base_url 为空字符串 → 视为未配置,直接跳过。"""
    client = CompatHTTPClient(base_url="", http_client=MagicMock())
    svc = _make_service(compat_client=client)
    r = svc.check_global("env-1")
    assert r.ok and r.value == []


def test_check_global_http_failure_degrades_to_empty():
    """HTTP 失败 → 降级返回空列表(不抛错)。"""
    http = MagicMock()
    http.post.return_value = Result.fail(ServiceError(
        code="HTTP_FAILED", message="timeout"))
    client = CompatHTTPClient(base_url="https://example.com", http_client=http)
    svc = _make_service(compat_client=client)
    r = svc.check_global("env-1")
    assert r.ok and r.value == []


def test_check_global_success_returns_conflicts():
    http = MagicMock()
    http.post.return_value = Result.ok([{
        "node_ids": ["pkg-a", "pkg-b"],
        "reason": "torch 1.x conflict",
    }])
    client = CompatHTTPClient(base_url="https://example.com", http_client=http)
    svc = _make_service(compat_client=client)
    svc.dep_repo.list_by_env = MagicMock(return_value=[
        {"dep_name": "torch", "dep_version_spec": "==1.0"},
    ])
    r = svc.check_global("env-1")
    assert r.ok
    assert len(r.value) == 1
    assert r.value[0]["conflict_type"] == "global_dep_known_incompat"