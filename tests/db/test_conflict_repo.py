"""ConflictRepo CRUD + 活跃索引查询测试。"""
from __future__ import annotations
from pathlib import Path
import pytest
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.db.conflict_repo import ConflictRepo
from comfy_mgr.models.conflict import Conflict


@pytest.fixture
def repo(tmp_path: Path) -> ConflictRepo:
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    # pre-insert env-1 / env-2 to satisfy FK on node_conflicts
    for env_id, name in [("env-1", "e1"), ("env-2", "e2")]:
        conn.execute(
            "INSERT INTO environments (id, name, root_path, comfyui_layout, "
            "venv_path, python_executable, custom_nodes_path, "
            "extra_model_paths_yaml, port) VALUES (?, ?, ?, 'shared', "
            "'/v', '/v/python', '/cn', '/emp.yaml', 8188)",
            (env_id, name, f"/{env_id}"),
        )
    return ConflictRepo(conn)


def _make_conflict(env_id: str = "env-1", **overrides) -> Conflict:
    defaults = dict(
        id="cf-aaa", env_id=env_id,
        conflict_type="duplicate_class",
        node_ids=["sn-x", "sn-y"],
        detail={"class": "KSampler"},
        detected_at="2026-06-26T00:00:00",
    )
    defaults.update(overrides)
    return Conflict(**defaults)


def test_insert_and_list_active(repo):
    repo.insert(_make_conflict())
    rows = repo.list_active("env-1")
    assert len(rows) == 1
    assert rows[0].detail == {"class": "KSampler"}


def test_resolve_marks_resolved_at(repo):
    repo.insert(_make_conflict())
    r = repo.resolve("cf-aaa")
    assert r.ok
    assert repo.list_active("env-1") == []  # 不再活跃
    assert repo.list_all("env-1")[0].resolved_at is not None


def test_ignore_marks_ignored_and_resolved(repo):
    repo.insert(_make_conflict())
    r = repo.ignore("cf-aaa")
    assert r.ok
    row = repo.list_all("env-1")[0]
    assert row.ignored == 1
    assert row.resolved_at is not None


def test_resolve_active_old_conflicts(repo):
    """recompute 流程:旧活跃冲突全部软删,新冲突插入。"""
    repo.insert(_make_conflict(id="cf-old"))
    repo.resolve_active("env-1")
    assert repo.list_active("env-1") == []


def test_node_ids_sorted_in_storage(repo):
    c = _make_conflict(node_ids=["sn-b", "sn-a"])
    repo.insert(c)
    fetched = repo.list_all("env-1")[0]
    assert fetched.node_ids == ["sn-a", "sn-b"]


def test_list_active_ignores_other_envs(repo):
    repo.insert(_make_conflict("env-1", id="cf-1"))
    repo.insert(_make_conflict("env-2", id="cf-2"))
    assert len(repo.list_active("env-1")) == 1
    assert len(repo.list_active("env-2")) == 1