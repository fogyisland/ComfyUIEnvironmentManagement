"""CatalogCacheRepo:缓存命中/过期/UNIQUE 测试。"""
from pathlib import Path
import uuid
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.db.catalog_repo import CatalogCacheRepo


def _make_entry(source_url="https://api.comfy.org/nodes",
                package="ComfyUI-Impact", **kw):
    base = {
        "id": f"cc-{uuid.uuid4().hex[:8]}",
        "source_url": source_url,
        "package": package,
        "raw_metadata": '{"name": "Impact"}',
        "cached_at": "2026-06-28T00:00:00",
        "expires_at": "2026-06-28T01:00:00",
    }
    base.update(kw)
    return base


def test_upsert_inserts(tmp_path: Path):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    repo = CatalogCacheRepo(conn)
    assert repo.upsert(_make_entry()).ok
    assert len(repo.list_all()) == 1


def test_upsert_replaces_on_conflict(tmp_path: Path):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    repo = CatalogCacheRepo(conn)
    repo.upsert(_make_entry(raw_metadata='{"v":1}'))
    repo.upsert(_make_entry(raw_metadata='{"v":2}'))
    rows = repo.list_all()
    assert len(rows) == 1
    assert '"v":2' in rows[0]["raw_metadata"]


def test_get_by_package(tmp_path: Path):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    repo = CatalogCacheRepo(conn)
    repo.upsert(_make_entry(package="p1"))
    repo.upsert(_make_entry(package="p2"))
    row = repo.get_by_package("p1")
    assert row["package"] == "p1"


def test_list_non_expired(tmp_path: Path):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    repo = CatalogCacheRepo(conn)
    repo.upsert(_make_entry(
        package="fresh", expires_at="2099-12-31T00:00:00"))
    repo.upsert(_make_entry(
        package="stale", expires_at="2020-01-01T01:00:00"))
    rows = repo.list_non_expired()
    assert len(rows) == 1
    assert rows[0]["package"] == "fresh"


def test_list_all_returns_stale_too(tmp_path: Path):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    repo = CatalogCacheRepo(conn)
    repo.upsert(_make_entry(package="stale", expires_at="2020-01-01T01:00:00"))
    repo.upsert(_make_entry(package="fresh", expires_at="2099-12-31T00:00:00"))
    assert len(repo.list_all()) == 2


def test_search_substring_case_insensitive(tmp_path: Path):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    repo = CatalogCacheRepo(conn)
    repo.upsert(_make_entry(package="ComfyUI-Impact-Pack"))
    repo.upsert(_make_entry(package="ComfyUI-Manager"))
    repo.upsert(_make_entry(package="Other"))
    rows = repo.search_substring("impact")
    assert len(rows) == 1
    assert rows[0]["package"] == "ComfyUI-Impact-Pack"
