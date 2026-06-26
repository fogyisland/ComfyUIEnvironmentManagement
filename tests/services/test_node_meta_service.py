"""NodeMetaService:GitHub 元数据本地缓存 + 1h TTL。"""
from __future__ import annotations
from datetime import datetime, timezone, timedelta
from pathlib import Path
from unittest.mock import MagicMock
import pytest
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.db.node_meta_repo import NodeMetaRepo
from comfy_mgr.infra.github_client import GitHubClient
from comfy_mgr.infra.pkg_meta import _now_iso
from comfy_mgr.services.node_meta import NodeMetaService
from comfy_mgr.models.node_meta import NodeMeta
from comfy_mgr.result import Result


@pytest.fixture
def mock_github():
    return MagicMock(spec=GitHubClient)


@pytest.fixture
def service(tmp_path, mock_github):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    return NodeMetaService(
        conn=conn, github=mock_github, cache_ttl_seconds=3600,
    ), conn, mock_github


def test_get_cached_returns_none_when_empty(service):
    svc, _, _ = service
    r = svc.get_cached("pkg-x")
    assert r.ok
    assert r.value is None


def test_get_or_fetch_calls_github_on_miss(service):
    svc, _, gh = service
    gh.get_repo_meta.return_value = Result.ok({
        "stars": 100, "last_commit": "2026-06-25T00:00:00Z",
        "homepage": None, "github_url": "https://github.com/x/y",
    })
    r = svc.get_or_fetch("pkg-x", "x", "y")
    assert r.ok
    assert r.value.stars == 100
    gh.get_repo_meta.assert_called_once_with("x", "y")


def test_get_or_fetch_uses_cache_within_ttl(service):
    svc, conn, gh = service
    # 预填一个新鲜的缓存
    NodeMetaRepo(conn).upsert(NodeMeta(
        package="pkg-x", stars=200, fetched_at=_now_iso(),
    ))
    r = svc.get_or_fetch("pkg-x", "x", "y")
    assert r.ok
    assert r.value.stars == 200
    gh.get_repo_meta.assert_not_called()


def test_get_or_fetch_refreshes_after_ttl(service):
    svc, conn, gh = service
    # 预填一个过期的缓存(2h 前)
    expired = (datetime.now(timezone.utc) - timedelta(hours=2)).isoformat(
        timespec="seconds")
    NodeMetaRepo(conn).upsert(NodeMeta(
        package="pkg-x", stars=200, fetched_at=expired,
    ))
    gh.get_repo_meta.return_value = Result.ok({
        "stars": 300, "last_commit": None, "homepage": None,
        "github_url": "https://github.com/x/y",
    })
    r = svc.get_or_fetch("pkg-x", "x", "y")
    assert r.ok
    assert r.value.stars == 300


def test_fetch_writes_error_to_cache_on_failure(service):
    """GitHub 拉失败,缓存里写 fetch_error,UI 友好提示。"""
    from comfy_mgr.result import ServiceError
    svc, conn, gh = service
    gh.get_repo_meta.return_value = Result.fail(
        ServiceError("META_FETCH_FAILED", "network"))
    r = svc.fetch("pkg-x", "x", "y")
    assert not r.ok
    # 缓存里写了 fetch_error
    cached = NodeMetaRepo(conn).get("pkg-x")
    assert cached is not None
    assert cached.fetch_error == "network"


def test_get_cached_returns_within_ttl_no_fetch(service):
    svc, conn, _ = service
    NodeMetaRepo(conn).upsert(NodeMeta(
        package="pkg-x", stars=42, fetched_at=_now_iso(),
    ))
    r = svc.get_cached("pkg-x")
    assert r.ok
    assert r.value.stars == 42
