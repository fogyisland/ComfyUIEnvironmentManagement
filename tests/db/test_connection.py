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
    assert ver == 5  # M4 T21: schema v5
    conn.close()

def test_schema_v2_creates_process_state(tmp_path):
    from comfy_mgr.db.connection import get_connection, init_schema, get_schema_version
    db = tmp_path / "test.db"
    conn = get_connection(db)
    init_schema(conn)
    assert get_schema_version(conn) >= 2
    row = conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name='process_state'"
    ).fetchone()
    assert row is not None
    cols = [r["name"] for r in conn.execute("PRAGMA table_info(process_state)").fetchall()]
    assert set(cols) >= {"env_id", "pid", "port", "started_at"}

def test_schema_v1_to_v2_migration(tmp_path):
    """v1 schema 应能被 init_schema 升级到 v2（process_state 表新增）。"""
    from comfy_mgr.db.connection import get_connection, init_schema
    db = tmp_path / "test.db"
    conn = get_connection(db)
    # 先建 v1 schema（手工）
    conn.executescript("""
        CREATE TABLE schema_version (version INTEGER PRIMARY KEY);
        INSERT INTO schema_version VALUES (1);
        CREATE TABLE nodes (id TEXT PRIMARY KEY, name TEXT, repo_url TEXT, local_path TEXT);
        CREATE TABLE environments (id TEXT PRIMARY KEY, name TEXT, root_path TEXT,
                                   comfyui_layout TEXT, comfyui_source TEXT, venv_path TEXT,
                                   python_executable TEXT, custom_nodes_path TEXT,
                                   extra_model_paths_yaml TEXT, port INTEGER,
                                   enabled_node_ids_json TEXT DEFAULT '[]',
                                   status TEXT DEFAULT 'stopped', pid INTEGER,
                                   created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                                   updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP);
    """)
    # 再 init（应该幂等加 process_state + v2 行）
    init_schema(conn)
    row = conn.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='process_state'").fetchone()
    assert row is not None
    assert get_schema_version(conn) >= 2
