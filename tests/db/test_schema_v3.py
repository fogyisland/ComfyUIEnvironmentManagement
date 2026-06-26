"""schema v3 验证:M2 三表存在 + 索引存在。"""
import sqlite3
from pathlib import Path
from comfy_mgr.db.connection import get_connection, init_schema, get_schema_version


def test_schema_v3_creates_scanned_nodes_table(tmp_path: Path):
    db = tmp_path / "test.db"
    conn = get_connection(db)
    init_schema(conn)
    row = conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name='scanned_nodes'"
    ).fetchone()
    assert row is not None


def test_schema_v3_creates_node_meta_cache_table(tmp_path: Path):
    db = tmp_path / "test.db"
    conn = get_connection(db)
    init_schema(conn)
    row = conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name='node_meta_cache'"
    ).fetchone()
    assert row is not None


def test_schema_v3_creates_node_conflicts_table(tmp_path: Path):
    db = tmp_path / "test.db"
    conn = get_connection(db)
    init_schema(conn)
    row = conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name='node_conflicts'"
    ).fetchone()
    assert row is not None


def test_schema_v3_idempotent(tmp_path: Path):
    """多次调用 init_schema 不报错。"""
    db = tmp_path / "test.db"
    conn = get_connection(db)
    init_schema(conn)
    init_schema(conn)  # 第二次不能挂
    init_schema(conn)


def test_schema_v3_preserves_m0_nodes_table(tmp_path: Path):
    """M0 的 nodes 表不动(M2 spec 调整:避免命名冲突)。"""
    db = tmp_path / "test.db"
    conn = get_connection(db)
    init_schema(conn)
    # M0 表的列存在
    cols = {row[1] for row in conn.execute("PRAGMA table_info(nodes)").fetchall()}
    assert "name" in cols
    assert "repo_url" in cols
    assert "local_path" in cols


def test_schema_v3_indexes_exist(tmp_path: Path):
    db = tmp_path / "test.db"
    conn = get_connection(db)
    init_schema(conn)
    idx_names = {
        row[0] for row in
        conn.execute("SELECT name FROM sqlite_master WHERE type='index'").fetchall()
    }
    assert "idx_scanned_nodes_env" in idx_names
    assert "idx_scanned_nodes_status" in idx_names
    assert "idx_conflicts_active" in idx_names
