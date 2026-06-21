import sqlite3
from pathlib import Path
from comfy_mgr.db.connection import get_connection, init_schema, get_schema_version

def test_get_connection_creates_file(tmp_path):
    db = tmp_path / "test.db"
    conn = get_connection(db)
    assert db.exists()
    conn.close()

def test_get_connection_enables_wal(tmp_path):
    db = tmp_path / "test.db"
    conn = get_connection(db)
    mode = conn.execute("PRAGMA journal_mode").fetchone()[0]
    assert mode.lower() == "wal"
    conn.close()

def test_init_schema_creates_all_tables(tmp_path):
    db = tmp_path / "test.db"
    conn = get_connection(db)
    init_schema(conn)
    tables = {r[0] for r in conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table'"
    ).fetchall()}
    assert "nodes" in tables
    assert "environments" in tables
    assert "conflicts_cache" in tables
    assert "known_incompat" in tables
    assert "schema_version" in tables
    conn.close()

def test_init_schema_is_idempotent(tmp_path):
    db = tmp_path / "test.db"
    conn = get_connection(db)
    init_schema(conn)
    init_schema(conn)  # 第二次不应报错
    conn.close()

def test_get_schema_version(tmp_path):
    db = tmp_path / "test.db"
    conn = get_connection(db)
    init_schema(conn)
    ver = get_schema_version(conn)
    assert ver == 1
    conn.close()