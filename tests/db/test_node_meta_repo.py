"""NodeMetaRepo CRUD 测试。"""
from __future__ import annotations
from pathlib import Path
import pytest
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.db.node_meta_repo import NodeMetaRepo
from comfy_mgr.models.node_meta import NodeMeta


@pytest.fixture
def repo(tmp_path: Path) -> NodeMetaRepo:
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    return NodeMetaRepo(conn)


def _make_meta(package: str = "pkg-a") -> NodeMeta:
    return NodeMeta(
        package=package,
        github_url=f"https://github.com/test/{package}",
        stars=100,
        last_commit="2026-06-25T00:00:00",
        homepage=None,
        fetched_at="2026-06-26T00:00:00",
    )


def test_upsert_inserts_new(repo):
    r = repo.upsert(_make_meta())
    assert r.ok
    assert repo.get("pkg-a") is not None


def test_upsert_overwrites_existing(repo):
    repo.upsert(_make_meta())
    repo.upsert(NodeMeta(
        package="pkg-a", github_url="https://other",
        stars=200, last_commit=None, homepage=None,
        fetched_at="2026-06-27T00:00:00", fetch_error="timeout",
    ))
    fetched = repo.get("pkg-a")
    assert fetched.stars == 200
    assert fetched.fetch_error == "timeout"


def test_get_returns_none_for_missing(repo):
    assert repo.get("nope") is None
