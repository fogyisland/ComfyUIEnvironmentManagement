from __future__ import annotations
import sqlite3
from dataclasses import dataclass
from datetime import datetime
from comfy_mgr.result import Result, ServiceError


@dataclass
class ProcessState:
    env_id: str
    pid: int
    port: int
    started_at: datetime


class ProcessStateRepo:
    def __init__(self, conn: sqlite3.Connection):
        self.conn = conn

    def save(self, state: ProcessState) -> Result[None]:
        try:
            self.conn.execute("""
                INSERT INTO process_state (env_id, pid, port, started_at)
                VALUES (?, ?, ?, ?)
                ON CONFLICT(env_id) DO UPDATE SET
                    pid=excluded.pid,
                    port=excluded.port,
                    started_at=excluded.started_at
            """, (
                state.env_id, state.pid, state.port,
                state.started_at.isoformat(),
            ))
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="PROCESS_STATE_SAVE_FAILED",
                message=str(e),
            ))

    def delete(self, env_id: str) -> Result[None]:
        self.conn.execute("DELETE FROM process_state WHERE env_id = ?", (env_id,))
        return Result.ok(None)

    def get(self, env_id: str) -> ProcessState | None:
        row = self.conn.execute(
            "SELECT * FROM process_state WHERE env_id = ?", (env_id,)
        ).fetchone()
        if not row:
            return None
        return ProcessState(
            env_id=row["env_id"],
            pid=row["pid"],
            port=row["port"],
            started_at=datetime.fromisoformat(row["started_at"]),
        )

    def list_all(self) -> list[ProcessState]:
        rows = self.conn.execute("SELECT * FROM process_state").fetchall()
        return [ProcessState(
            env_id=r["env_id"], pid=r["pid"], port=r["port"],
            started_at=datetime.fromisoformat(r["started_at"]),
        ) for r in rows]
