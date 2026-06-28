"""VersionRepo CRUD + CASCADE + limit 测试。"""
from pathlib import Path
import uuid
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.db.version_repo import VersionRepo


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


def _make_record(env_id="env-1", package="p1", action="upgrade",
                 result="success", **kw):
    base = {
        "id": f"vh-{uuid.uuid4().hex[:8]}",
        "env_id": env_id,
        "package": package,
        "action": action,
        "version_before": "abc123",
        "version_after": "def456",
        "result": result,
        "error_message": None,
        "performed_at": "2026-06-28T00:00:00",
    }
    base.update(kw)
    return base


def test_insert_and_get(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    repo = VersionRepo(conn)
    rec = _make_record()
    assert repo.insert(rec).ok
    got = repo.get(rec["id"])
    assert got["package"] == "p1"
    assert got["action"] == "upgrade"


def test_list_by_env(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    repo = VersionRepo(conn)
    repo.insert(_make_record(package="p1"))
    repo.insert(_make_record(package="p2"))
    rows = repo.list_by_env("env-1")
    assert len(rows) == 2


def test_list_by_env_and_package_respects_limit(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    repo = VersionRepo(conn)
    for i in range(5):
        repo.insert(_make_record(
            package="p1", performed_at=f"2026-06-2{i}T00:00:00",
        ))
    rows = repo.list_by_env_and_package("env-1", "p1", limit=3)
    assert len(rows) == 3
    assert rows[0]["performed_at"] == "2026-06-24T00:00:00"


def test_cascade_delete_env(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    repo = VersionRepo(conn)
    rec = _make_record()
    repo.insert(rec)
    conn.execute("DELETE FROM environments WHERE id='env-1'")
    assert repo.get(rec["id"]) is None


def test_get_returns_none_for_missing(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    repo = VersionRepo(conn)
    assert repo.get("vh-nope") is None
