"""schema v4 验证:M3 三表 + scanned_nodes.locked 列 + CASCADE FK。"""
from pathlib import Path
from comfy_mgr.db.connection import (
    get_connection, init_schema, CURRENT_SCHEMA_VERSION, get_schema_version,
)


def _init(tmp_path: Path):
    db = tmp_path / "test.db"
    conn = get_connection(db)
    init_schema(conn)
    conn.execute(
        "INSERT INTO environments (id, name, root_path, comfyui_layout, "
        "venv_path, python_executable, custom_nodes_path, "
        "extra_model_paths_yaml, port) "
        "VALUES ('env-1','e1','/e1','shared','/e1/.venv','/e1/.venv/python',"
        "'/e1/custom_nodes','/e1/emp.yaml',8188)"
    )
    return conn


def test_current_schema_version_is_4():
    assert CURRENT_SCHEMA_VERSION == 4


def test_schema_version_record_is_4(tmp_path: Path):
    conn = _init(tmp_path)
    assert get_schema_version(conn) == 4


def test_v4_creates_version_history_table(tmp_path: Path):
    conn = _init(tmp_path)
    row = conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name='version_history'"
    ).fetchone()
    assert row is not None


def test_v4_creates_dep_records_table(tmp_path: Path):
    conn = _init(tmp_path)
    row = conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name='dep_records'"
    ).fetchone()
    assert row is not None


def test_v4_creates_catalog_cache_table(tmp_path: Path):
    conn = _init(tmp_path)
    row = conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name='catalog_cache'"
    ).fetchone()
    assert row is not None


def test_v4_scanned_nodes_has_locked_column(tmp_path: Path):
    conn = _init(tmp_path)
    cols = {row[1] for row in conn.execute("PRAGMA table_info(scanned_nodes)").fetchall()}
    assert "locked" in cols


def test_v4_scanned_nodes_locked_default_0(tmp_path: Path):
    conn = _init(tmp_path)
    conn.execute(
        "INSERT INTO scanned_nodes (id, env_id, package, package_path) "
        "VALUES ('sn-aaa','env-1','p1','/e1/custom_nodes/p1')"
    )
    row = conn.execute("SELECT locked FROM scanned_nodes WHERE id='sn-aaa'").fetchone()
    assert row["locked"] == 0


def test_v4_indexes_exist(tmp_path: Path):
    conn = _init(tmp_path)
    idx = {
        r[0] for r in
        conn.execute("SELECT name FROM sqlite_master WHERE type='index'").fetchall()
    }
    assert "idx_version_history_env" in idx
    assert "idx_dep_records_env" in idx
    assert "idx_dep_records_dep" in idx
    assert "idx_catalog_cache_source" in idx


def test_v4_cascade_delete_env_clears_version_history(tmp_path: Path):
    conn = _init(tmp_path)
    conn.execute(
        "INSERT INTO version_history (id, env_id, package, action, result, performed_at) "
        "VALUES ('vh-aaa','env-1','p1','upgrade','success','2026-06-28T00:00:00')"
    )
    conn.execute("DELETE FROM environments WHERE id='env-1'")
    row = conn.execute("SELECT id FROM version_history WHERE id='vh-aaa'").fetchone()
    assert row is None


def test_v4_idempotent(tmp_path: Path):
    db = tmp_path / "test.db"
    conn = get_connection(db)
    init_schema(conn)
    init_schema(conn)


def test_v4_preserves_v3_tables(tmp_path: Path):
    conn = _init(tmp_path)
    tables = {
        r[0] for r in
        conn.execute("SELECT name FROM sqlite_master WHERE type='table'").fetchall()
    }
    assert "scanned_nodes" in tables
    assert "node_conflicts" in tables
    assert "node_meta_cache" in tables
