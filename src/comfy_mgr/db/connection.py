from __future__ import annotations
import sqlite3
from pathlib import Path

CURRENT_SCHEMA_VERSION = 3

def get_connection(db_path: Path) -> sqlite3.Connection:
    """打开 SQLite 连接，启用 WAL 模式。"""
    db_path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(str(db_path), isolation_level=None)  # autocommit
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA foreign_keys=ON")
    conn.row_factory = sqlite3.Row
    return conn

def init_schema(conn: sqlite3.Connection) -> None:
    """初始化 schema（幂等；支持 v1 → v2 → v3 migration）。"""
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

        CREATE TABLE IF NOT EXISTS process_state (
            env_id TEXT PRIMARY KEY,
            pid INTEGER NOT NULL,
            port INTEGER NOT NULL,
            started_at TIMESTAMP NOT NULL,
            FOREIGN KEY (env_id) REFERENCES environments(id) ON DELETE CASCADE
        );

        -- ========== M2 schema v3 增量 ==========
        CREATE TABLE IF NOT EXISTS scanned_nodes (
            id              TEXT PRIMARY KEY,
            env_id          TEXT NOT NULL
                            REFERENCES environments(id) ON DELETE CASCADE,
            package         TEXT NOT NULL,
            package_path    TEXT NOT NULL,
            version         TEXT,
            author          TEXT,
            description     TEXT,
            class_mappings  TEXT NOT NULL DEFAULT '[]',
            status          TEXT NOT NULL DEFAULT 'enabled'
                            CHECK(status IN ('enabled','disabled')),
            scan_meta       TEXT NOT NULL DEFAULT '{}',
            last_scanned_at TEXT,
            UNIQUE(env_id, package)
        );
        CREATE INDEX IF NOT EXISTS idx_scanned_nodes_env
            ON scanned_nodes(env_id);
        CREATE INDEX IF NOT EXISTS idx_scanned_nodes_status
            ON scanned_nodes(env_id, status);

        CREATE TABLE IF NOT EXISTS node_meta_cache (
            package         TEXT PRIMARY KEY,
            github_url      TEXT,
            stars           INTEGER,
            last_commit     TEXT,
            homepage        TEXT,
            fetched_at      TEXT NOT NULL,
            fetch_error     TEXT
        );

        CREATE TABLE IF NOT EXISTS node_conflicts (
            id              TEXT PRIMARY KEY,
            env_id          TEXT NOT NULL
                            REFERENCES environments(id) ON DELETE CASCADE,
            conflict_type   TEXT NOT NULL
                            CHECK(conflict_type IN
                                  ('duplicate_class','version_mismatch','missing_dep')),
            node_ids        TEXT NOT NULL,
            detail          TEXT NOT NULL,
            detected_at     TEXT NOT NULL,
            resolved_at     TEXT,
            ignored         INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_conflicts_active
            ON node_conflicts(env_id) WHERE resolved_at IS NULL;
        -- ========== M2 schema v3 增量 END ==========

        INSERT OR IGNORE INTO schema_version (version) VALUES (1);
        INSERT OR IGNORE INTO schema_version (version) VALUES (2);
        INSERT OR IGNORE INTO schema_version (version) VALUES (3);
    """)

def get_schema_version(conn: sqlite3.Connection) -> int:
    row = conn.execute("SELECT MAX(version) FROM schema_version").fetchone()
    return row[0] if row[0] is not None else 0
