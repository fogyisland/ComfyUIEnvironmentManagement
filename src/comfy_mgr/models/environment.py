from __future__ import annotations
import json
import sqlite3
import uuid
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Literal
from comfy_mgr.result import Result, ServiceError

PORT_BASE = 8188

@dataclass
class Environment:
    id: str
    name: str
    root_path: Path
    comfyui_layout: Literal["shared", "independent"]
    comfyui_source: Path | None
    venv_path: Path
    python_executable: Path
    custom_nodes_path: Path
    extra_model_paths_yaml: Path
    port: int
    enabled_node_ids: list[str] = field(default_factory=list)
    status: Literal["stopped", "running", "error"] = "stopped"
    pid: int | None = None

class EnvironmentRepo:
    def __init__(self, conn: sqlite3.Connection):
        self.conn = conn

    def save(self, env: Environment) -> Result[None]:
        try:
            self.conn.execute("""
                INSERT INTO environments (
                    id, name, root_path, comfyui_layout, comfyui_source,
                    venv_path, python_executable, custom_nodes_path,
                    extra_model_paths_yaml, port, enabled_node_ids_json,
                    status, pid, updated_at
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, CURRENT_TIMESTAMP)
                ON CONFLICT(id) DO UPDATE SET
                    name=excluded.name,
                    root_path=excluded.root_path,
                    comfyui_layout=excluded.comfyui_layout,
                    comfyui_source=excluded.comfyui_source,
                    venv_path=excluded.venv_path,
                    python_executable=excluded.python_executable,
                    custom_nodes_path=excluded.custom_nodes_path,
                    extra_model_paths_yaml=excluded.extra_model_paths_yaml,
                    port=excluded.port,
                    enabled_node_ids_json=excluded.enabled_node_ids_json,
                    status=excluded.status,
                    pid=excluded.pid,
                    updated_at=CURRENT_TIMESTAMP
            """, (
                env.id, env.name, str(env.root_path), env.comfyui_layout,
                str(env.comfyui_source) if env.comfyui_source else None,
                str(env.venv_path), str(env.python_executable),
                str(env.custom_nodes_path), str(env.extra_model_paths_yaml),
                env.port, json.dumps(env.enabled_node_ids),
                env.status, env.pid,
            ))
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="ENV_SAVE_FAILED",
                message=str(e),
            ))

    def get(self, env_id: str) -> Environment | None:
        row = self.conn.execute(
            "SELECT * FROM environments WHERE id = ?", (env_id,)
        ).fetchone()
        if not row:
            return None
        return self._row_to_env(row)

    def list_all(self) -> list[Environment]:
        rows = self.conn.execute(
            "SELECT * FROM environments ORDER BY name"
        ).fetchall()
        return [self._row_to_env(r) for r in rows]

    def delete(self, env_id: str) -> Result[None]:
        cursor = self.conn.execute(
            "DELETE FROM environments WHERE id = ?", (env_id,)
        )
        if cursor.rowcount == 0:
            return Result.fail(ServiceError(
                code="ENV_NOT_FOUND",
                message=f"环境 {env_id} 不存在",
            ))
        return Result.ok(None)

    def _row_to_env(self, row) -> Environment:
        d = dict(row)
        return Environment(
            id=d["id"],
            name=d["name"],
            root_path=Path(d["root_path"]),
            comfyui_layout=d["comfyui_layout"],
            comfyui_source=Path(d["comfyui_source"]) if d["comfyui_source"] else None,
            venv_path=Path(d["venv_path"]),
            python_executable=Path(d["python_executable"]),
            custom_nodes_path=Path(d["custom_nodes_path"]),
            extra_model_paths_yaml=Path(d["extra_model_paths_yaml"]),
            port=d["port"],
            enabled_node_ids=json.loads(d["enabled_node_ids_json"] or "[]"),
            status=d["status"],
            pid=d["pid"],
        )

def generate_env_id() -> str:
    return f"env-{uuid.uuid4().hex[:8]}"