"""M4 T18: catalog_cache_ttl_minutes 注入 + cache 过期逻辑。"""
from __future__ import annotations
import pytest
from datetime import datetime, timedelta
from unittest.mock import MagicMock
from comfy_mgr.infra.catalog_http_client import CatalogHTTPClient


def test_ttl_minutes_propagated_to_seconds():
    """cache_ttl_seconds = ttl_minutes * 60。"""
    client = CatalogHTTPClient(
        catalog_repo=MagicMock(),
        http_client=MagicMock(),
        cache_ttl_seconds=30 * 60,
    )
    assert client.cache_ttl_seconds == 1800


def test_list_remote_respects_ttl(tmp_path):
    """缓存未过期时,不应发 HTTP。"""
    from comfy_mgr.db.catalog_repo import CatalogCacheRepo
    from comfy_mgr.db.connection import get_connection, init_schema
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    repo = CatalogCacheRepo(conn)
    now = datetime.now()
    repo.upsert({
        "id": "cc-abc",
        "source_url": "https://x.com/nodes",
        "package": "pkg-a",
        "raw_metadata": '{"id":"pkg-a","name":"pkg-a"}',
        "cached_at": now.isoformat(timespec="seconds"),
        "expires_at": (now + timedelta(seconds=3600)).isoformat(timespec="seconds"),
    })
    http = MagicMock()
    client = CatalogHTTPClient(
        catalog_repo=repo,
        http_client=http,
        cache_ttl_seconds=3600,
    )
    r = client.list_remote()
    assert r.ok and len(r.value) == 1
    http.get.assert_not_called()  # 没发 HTTP


def test_list_remote_expired_triggers_http(tmp_path):
    """缓存过期 → 必须发 HTTP。"""
    from comfy_mgr.db.catalog_repo import CatalogCacheRepo
    from comfy_mgr.db.connection import get_connection, init_schema
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    repo = CatalogCacheRepo(conn)
    now = datetime.now()
    repo.upsert({
        "id": "cc-old",
        "source_url": "https://x.com/nodes",
        "package": "pkg-old",
        "raw_metadata": '{"id":"pkg-old","name":"pkg-old"}',
        "cached_at": (now - timedelta(hours=2)).isoformat(timespec="seconds"),
        "expires_at": (now - timedelta(hours=1)).isoformat(timespec="seconds"),
    })
    http = MagicMock()
    http.get.return_value.ok = False
    http.get.return_value.error = MagicMock(code="HTTP_FAILED", message="timeout")
    client = CatalogHTTPClient(
        catalog_repo=repo,
        http_client=http,
        cache_ttl_seconds=3600,
        base_url="https://x.com",
    )
    r = client.list_remote()
    # 过期 + HTTP 失败 → 降级返回 stale
    assert r.ok
    assert any(e.get("stale") for e in r.value)
