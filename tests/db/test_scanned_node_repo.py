"""ScannedNodeRepo CRUD + env 隔离测试。"""
from __future__ import annotations
from pathlib import Path
import pytest
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.db.scanned_node_repo import ScannedNodeRepo
from comfy_mgr.models.scanned_node import ScannedNode


@pytest.fixture
def repo(tmp_path: Path) -> ScannedNodeRepo:
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    # pre-insert env-1 and env-2 to satisfy FK on scanned_nodes
    for env_id, name in [("env-1", "e1"), ("env-2", "e2")]:
        conn.execute(
            "INSERT INTO environments (id, name, root_path, comfyui_layout, "
            "venv_path, python_executable, custom_nodes_path, "
            "extra_model_paths_yaml, port) VALUES (?, ?, ?, 'shared', "
            "'/v', '/v/python', '/cn', '/emp.yaml', 8188)",
            (env_id, name, f"/{env_id}"),
        )
    return ScannedNodeRepo(conn)


def _make_node(env_id: str = "env-1", package: str = "pkg-a") -> ScannedNode:
    # 默认 id 是 "sn-aaa" (供 test_set_status / test_upsert_updates 用),
    # 显式指定 package 时用派生 id 避免 PK 冲突。
    if package == "pkg-a":
        node_id = "sn-aaa"
    else:
        suffix = f"{env_id}{package}".replace("-", "")[:8]
        node_id = f"sn-{suffix}"
    return ScannedNode(
        id=node_id, env_id=env_id, package=package,
        package_path=Path(f"/envs/{env_id}/custom_nodes/{package}"),
        class_mappings=["A", "B"],
        scan_meta={"source": "ast_clean", "warnings": []},
    )


def test_upsert_inserts_new(repo):
    r = repo.upsert(_make_node())
    assert r.ok
    assert repo.count() == 1


def test_upsert_updates_existing_on_uniq_env_package(repo):
    n = _make_node()
    repo.upsert(n)
    n.class_mappings = ["A", "B", "C"]
    repo.upsert(n)
    fetched = repo.get("sn-aaa")
    assert fetched.class_mappings == ["A", "B", "C"]


def test_list_by_env_filters(repo):
    repo.upsert(_make_node("env-1", "pkg-a"))
    repo.upsert(_make_node("env-1", "pkg-b"))
    repo.upsert(_make_node("env-2", "pkg-a"))
    rows = repo.list_by_env("env-1")
    assert len(rows) == 2
    assert {r.package for r in rows} == {"pkg-a", "pkg-b"}


def test_list_enabled_filters_disabled(repo):
    n1 = _make_node("env-1", "pkg-a")
    n2 = _make_node("env-1", "pkg-b")
    n2.id = "sn-bbb"
    n2.status = "disabled"
    repo.upsert(n1)
    repo.upsert(n2)
    rows = repo.list_enabled("env-1")
    assert len(rows) == 1
    assert rows[0].package == "pkg-a"


def test_set_status_updates(repo):
    repo.upsert(_make_node())
    r = repo.set_status("sn-aaa", "disabled")
    assert r.ok
    assert repo.get("sn-aaa").status == "disabled"


def test_get_returns_none_for_missing(repo):
    assert repo.get("nope") is None


def test_delete_by_env_cascades(tmp_path: Path):
    """环境删除时,scanned_nodes 通过 FK ON DELETE CASCADE 自动清。"""
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    # 插一个 env + 2 个 node
    conn.execute(
        "INSERT INTO environments (id, name, root_path, comfyui_layout, "
        "venv_path, python_executable, custom_nodes_path, extra_model_paths_yaml, port) "
        "VALUES ('env-1', 'e1', '/e1', 'shared', '/e1/.venv', '/e1/.venv/python', "
        "'/e1/custom_nodes', '/e1/emp.yaml', 8188)"
    )
    repo = ScannedNodeRepo(conn)
    repo.upsert(_make_node("env-1", "pkg-a"))
    repo.upsert(_make_node("env-1", "pkg-b"))
    # 删 env
    conn.execute("DELETE FROM environments WHERE id='env-1'")
    assert repo.count() == 0