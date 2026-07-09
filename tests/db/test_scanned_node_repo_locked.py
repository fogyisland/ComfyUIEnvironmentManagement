"""M4 T19：scanned_nodes.locked + disable_mode 列读写。"""
from __future__ import annotations
import pytest
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.db.scanned_node_repo import ScannedNodeRepo
from comfy_mgr.models.scanned_node import ScannedNode
from pathlib import Path


@pytest.fixture
def repo(tmp_path):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    # pre-insert env-1 to satisfy FK on scanned_nodes(env_id)
    conn.execute(
        "INSERT INTO environments (id, name, root_path, comfyui_layout, "
        "venv_path, python_executable, custom_nodes_path, "
        "extra_model_paths_yaml, port) VALUES ('env-1', 'e1', '/env-1', "
        "'shared', '/v', '/v/python', '/cn', '/emp.yaml', 8188)"
    )
    return ScannedNodeRepo(conn)


def test_locked_field_roundtrip(repo):
    n = ScannedNode(
        id="sn-1", env_id="env-1", package="pkg-a",
        package_path=Path("/fake/pkg-a"), locked=True,
    )
    repo.upsert(n)
    got = repo.get("sn-1")
    assert got is not None
    assert got.locked is True


def test_locked_default_false(repo):
    n = ScannedNode(
        id="sn-1", env_id="env-1", package="pkg-a",
        package_path=Path("/fake/pkg-a"),
    )
    repo.upsert(n)
    got = repo.get("sn-1")
    assert got.locked is False


def test_locked_filter_in_list(repo):
    n_locked = ScannedNode(
        id="sn-1", env_id="env-1", package="pkg-locked",
        package_path=Path("/fake/locked"), locked=True,
    )
    n_unlocked = ScannedNode(
        id="sn-2", env_id="env-1", package="pkg-unlocked",
        package_path=Path("/fake/unlocked"), locked=False,
    )
    repo.upsert(n_locked)
    repo.upsert(n_unlocked)
    all_nodes = repo.list_by_env("env-1")
    assert len(all_nodes) == 2
    locked_set = {n.package for n in all_nodes if n.locked}
    assert locked_set == {"pkg-locked"}


def test_disable_mode_default_db_flag(repo):
    n = ScannedNode(
        id="sn-1", env_id="env-1", package="pkg-a",
        package_path=Path("/fake/pkg-a"),
    )
    repo.upsert(n)
    got = repo.get("sn-1")
    assert got.disable_mode == "db_flag"
