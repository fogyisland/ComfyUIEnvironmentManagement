from __future__ import annotations
import sqlite3
from pathlib import Path

CURRENT_SCHEMA_VERSION = 1

def get_connection(db_path: Path) -> sqlite3.Connection:
    """打开 SQLite 连接，启用 WAL 模式。"""
    db_path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(str(db_path), isolation_level=None)  # autocommit
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA foreign_keys=ON")
    conn.row_factory = sqlite3.Row
    return conn

def init_schema(conn: sqlite3.Connection) -> None:
    """初始化 schema（幂等）。"""
    conn.executescript("""
        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER PRIMARY KEY,
            applied_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS nodes (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            repo_url TEXT NOT NULL UNIQUE,
            local_path TEXT NOT NULL,
            current_version TEXT,
            description TEXT,
            author TEXT,
            metadata_json TEXT,
            last_analyzed_at TIMESTAMP,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS environments (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL UNIQUE,
            root_path TEXT NOT NULL,
            comfyui_layout TEXT NOT NULL,
            comfyui_source TEXT,
            venv_path TEXT,
            python_executable TEXT,
            custom_nodes_path TEXT,
            extra_model_paths_yaml TEXT,
            port INTEGER,
            enabled_node_ids_json TEXT DEFAULT '[]',
            status TEXT DEFAULT 'stopped',
            pid INTEGER,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS conflicts_cache (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            node_ids_hash TEXT NOT NULL,
            conflicts_json TEXT NOT NULL,
            computed_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS known_incompat (
            node_id_a TEXT,
            node_id_b TEXT,
            severity TEXT,
            note TEXT,
            PRIMARY KEY (node_id_a, node_id_b)
        );

        INSERT OR IGNORE INTO schema_version (version) VALUES (1);
    """)

def get_schema_version(conn: sqlite3.Connection) -> int:
    row = conn.execute("SELECT MAX(version) FROM schema_version").fetchone()
    return row[0] if row[0] is not None else 0