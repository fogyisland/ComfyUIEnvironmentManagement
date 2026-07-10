"""CatalogHTTPClient:cache hit/expire/降级。"""
from pathlib import Path
import json
from datetime import datetime, timedelta
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.db.catalog_repo import CatalogCacheRepo
from comfy_mgr.infra.catalog_http_client import CatalogHTTPClient


class MockHTTPClient:
    def __init__(self, payload):
        self.payload = payload
        self.calls = []

    def get(self, url, *, params=None):
        from comfy_mgr.result import Result, ServiceError
        self.calls.append(url)
        if self.payload is None:
            return Result.fail(ServiceError(
                code="HTTP_CONNECTION_FAILED", message="offline"))
        return Result.ok(self.payload)


def _bootstrap(tmp_path: Path):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    repo = CatalogCacheRepo(conn)
    return conn, repo


def test_list_remote_fetches_and_caches(tmp_path: Path):
    conn, repo = _bootstrap(tmp_path)
    mock_http = MockHTTPClient([
        {"id": "ComfyUI-Impact", "name": "Impact", "stars": 100,
         "author": "Dr.Lt.Data", "repo": "https://github.com/ltdrdata/ComfyUI-Impact-Pack"},
        {"id": "ComfyUI-Manager", "name": "Manager", "stars": 200,
         "author": "ltdrdata", "repo": "https://github.com/ltdrdata/ComfyUI-Manager"},
    ])
    client = CatalogHTTPClient(
        catalog_repo=repo, http_client=mock_http,
        base_url="https://api.comfy.org", cache_ttl_seconds=3600,
    )
    r = client.list_remote()
    assert r.ok
    assert len(r.value) == 2
    cached = repo.list_all()
    assert len(cached) == 2


def test_list_remote_uses_cache_when_fresh(tmp_path: Path):
    conn, repo = _bootstrap(tmp_path)
    # 预填一个未过期的 cache
    now = datetime.now().isoformat(timespec="seconds")
    later = (datetime.now() + timedelta(hours=1)).isoformat(timespec="seconds")
    repo.upsert({
        "id": "cc-x", "source_url": "https://api.comfy.org/nodes",
        "package": "Cached-Pkg",
        "raw_metadata": json.dumps({"id": "Cached-Pkg", "stars": 50}),
        "cached_at": now, "expires_at": later,
    })
    mock_http = MockHTTPClient(None)  # 如果 http 被调就 fail
    client = CatalogHTTPClient(
        catalog_repo=repo, http_client=mock_http,
        base_url="https://api.comfy.org",
    )
    r = client.list_remote()
    assert r.ok
    assert len(r.value) == 1
    assert r.value[0]["id"] == "Cached-Pkg"
    assert mock_http.calls == []  # 没调 HTTP


def test_list_remote_offline_returns_stale(tmp_path: Path):
    conn, repo = _bootstrap(tmp_path)
    # 预填一个已过期的 cache
    repo.upsert({
        "id": "cc-x", "source_url": "https://api.comfy.org/nodes",
        "package": "Stale-Pkg",
        "raw_metadata": json.dumps({"id": "Stale-Pkg"}),
        "cached_at": "2020-01-01T00:00:00",
        "expires_at": "2020-01-01T01:00:00",
    })
    mock_http = MockHTTPClient(None)  # offline
    client = CatalogHTTPClient(
        catalog_repo=repo, http_client=mock_http,
        base_url="https://api.comfy.org",
    )
    r = client.list_remote()
    assert r.ok
    assert r.value[0]["id"] == "Stale-Pkg"
    assert r.value[0].get("stale") is True


def test_list_remote_force_refresh_bypasses_cache(tmp_path: Path):
    conn, repo = _bootstrap(tmp_path)
    now = datetime.now().isoformat(timespec="seconds")
    later = (datetime.now() + timedelta(hours=1)).isoformat(timespec="seconds")
    repo.upsert({
        "id": "cc-x", "source_url": "https://api.comfy.org/nodes",
        "package": "Old-Pkg",
        "raw_metadata": json.dumps({"id": "Old-Pkg"}),
        "cached_at": now, "expires_at": later,
    })
    mock_http = MockHTTPClient([{"id": "New-Pkg", "stars": 10}])
    client = CatalogHTTPClient(
        catalog_repo=repo, http_client=mock_http,
        base_url="https://api.comfy.org",
    )
    r = client.list_remote(force_refresh=True)
    assert r.ok
    assert r.value[0]["id"] == "New-Pkg"
    assert mock_http.calls == ["https://api.comfy.org/nodes"]


def test_search_remote_uses_local_cache(tmp_path: Path):
    conn, repo = _bootstrap(tmp_path)
    now = datetime.now().isoformat(timespec="seconds")
    later = (datetime.now() + timedelta(hours=1)).isoformat(timespec="seconds")
    for pkg in ["ComfyUI-Impact-Pack", "ComfyUI-Manager", "Other"]:
        repo.upsert({
            "id": f"cc-{pkg}", "source_url": "x",
            "package": pkg,
            "raw_metadata": json.dumps({"id": pkg}),
            "cached_at": now, "expires_at": later,
        })
    mock_http = MockHTTPClient(None)
    client = CatalogHTTPClient(
        catalog_repo=repo, http_client=mock_http,
        base_url="https://api.comfy.org",
    )
    r = client.search_remote("impact")
    assert r.ok
    assert len(r.value) == 1
    assert r.value[0]["id"] == "ComfyUI-Impact-Pack"
    assert mock_http.calls == []  # 不发 HTTP


def test_get_remote_returns_stale_if_offline(tmp_path: Path):
    conn, repo = _bootstrap(tmp_path)
    repo.upsert({
        "id": "cc-x", "source_url": "x",
        "package": "p1",
        "raw_metadata": json.dumps({"id": "p1", "stars": 5}),
        "cached_at": "2020-01-01T00:00:00",
        "expires_at": "2020-01-01T01:00:00",
    })
    mock_http = MockHTTPClient(None)
    client = CatalogHTTPClient(
        catalog_repo=repo, http_client=mock_http,
        base_url="https://api.comfy.org",
    )
    r = client.get_remote("p1")
    assert r.ok
    assert r.value["stale"] is True
