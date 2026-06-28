"""DepRepo CRUD + UNIQUE 去重测试。"""
from pathlib import Path
import uuid
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.db.dep_repo import DepRepo


def _bootstrap(tmp_path: Path):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    conn.execute(
        "INSERT INTO environments (id, name, root_path, comfyui_layout, "
        "venv_path, python_executable, custom_nodes_path, "
        "extra_model_paths_yaml, port) "
        "VALUES ('env-1','e1','/e1','shared','/e1/.venv','/e1/.venv/python',"
        "'/e1/custom_nodes','/e1/emp.yaml',8188)"
    )
    return conn


def _make_dep(env_id="env-1", package="p1", source="requirements_txt",
              dep_name="torch", dep_version_spec=">=2.0", **kw):
    base = {
        "id": f"dr-{uuid.uuid4().hex[:8]}",
        "env_id": env_id,
        "package": package,
        "source": source,
        "dep_name": dep_name,
        "dep_version_spec": dep_version_spec,
        "scanned_at": "2026-06-28T00:00:00",
    }
    base.update(kw)
    return base


def test_upsert_inserts(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    repo = DepRepo(conn)
    assert repo.upsert(_make_dep()).ok
    assert len(repo.list_by_env("env-1")) == 1


def test_upsert_replaces_on_conflict(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    repo = DepRepo(conn)
    repo.upsert(_make_dep(dep_name="torch", dep_version_spec=">=1.0"))
    repo.upsert(_make_dep(dep_name="torch", dep_version_spec=">=2.0"))
    rows = repo.list_by_env("env-1")
    assert len(rows) == 1
    assert rows[0]["dep_version_spec"] == ">=2.0"


def test_delete_by_package(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    repo = DepRepo(conn)
    repo.upsert(_make_dep(package="p1", dep_name="torch"))
    repo.upsert(_make_dep(package="p1", dep_name="numpy"))
    repo.upsert(_make_dep(package="p2", dep_name="torch"))
    assert repo.delete_by_package("env-1", "p1") == 2
    assert len(repo.list_by_env("env-1")) == 1


def test_list_by_env_and_dep(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    repo = DepRepo(conn)
    repo.upsert(_make_dep(package="p1", dep_name="torch"))
    repo.upsert(_make_dep(package="p2", dep_name="torch"))
    repo.upsert(_make_dep(package="p2", dep_name="numpy"))
    rows = repo.list_by_env_and_dep("env-1", "torch")
    assert len(rows) == 2


def test_cascade_delete_env(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    repo = DepRepo(conn)
    repo.upsert(_make_dep())
    conn.execute("DELETE FROM environments WHERE id='env-1'")
    assert repo.list_by_env("env-1") == []
