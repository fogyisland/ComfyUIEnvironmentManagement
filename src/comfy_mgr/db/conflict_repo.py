"""ConflictRepo:node_conflicts 表的 CRUD。"""
from __future__ import annotations
import sqlite3
from datetime import datetime, timezone
from comfy_mgr.models.conflict import Conflict
from comfy_mgr.result import Result, ServiceError


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


class ConflictRepo:
    def __init__(self, conn: sqlite3.Connection):
        self.conn = conn

    def insert(self, conflict: Conflict) -> Result[None]:
        try:
            r = conflict.to_row()
            self.conn.execute("""
                INSERT INTO node_conflicts
                    (id, env_id, conflict_type, node_ids, detail,
                     detected_at, resolved_at, ignored)
                VALUES (:id, :env_id, :conflict_type, :node_ids, :detail,
                        :detected_at, :resolved_at, :ignored)
            """, r)
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="CONFLICT_SAVE_FAILED", message=str(e)))

    def list_active(self, env_id: str) -> list[Conflict]:
        rows = self.conn.execute("""
            SELECT * FROM node_conflicts
            WHERE env_id=? AND resolved_at IS NULL AND ignored=0
            ORDER BY detected_at
        """, (env_id,)).fetchall()
        return [Conflict.from_row(r) for r in rows]

    def list_all(self, env_id: str) -> list[Conflict]:
        rows = self.conn.execute(
            "SELECT * FROM node_conflicts WHERE env_id=? ORDER BY detected_at",
            (env_id,),
        ).fetchall()
        return [Conflict.from_row(r) for r in rows]

    def get(self, conflict_id: str) -> Conflict | None:
        row = self.conn.execute(
            "SELECT * FROM node_conflicts WHERE id=?", (conflict_id,)
        ).fetchone()
        return Conflict.from_row(row) if row else None

    def resolve(self, conflict_id: str) -> Result[None]:
        try:
            cursor = self.conn.execute(
                "UPDATE node_conflicts SET resolved_at=? "
                "WHERE id=? AND resolved_at IS NULL",
                (_now_iso(), conflict_id),
            )
            if cursor.rowcount == 0:
                return Result.fail(ServiceError(
                    code="CONFLICT_NOT_FOUND",
                    message=f"冲突 {conflict_id} 不存在或已解决"))
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="CONFLICT_SAVE_FAILED", message=str(e)))

    def ignore(self, conflict_id: str) -> Result[None]:
        try:
            self.conn.execute(
                "UPDATE node_conflicts SET ignored=1, resolved_at=? WHERE id=?",
                (_now_iso(), conflict_id),
            )
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="CONFLICT_SAVE_FAILED", message=str(e)))

    def resolve_active(self, env_id: str) -> Result[None]:
        """recompute 流程:旧活跃冲突全部软删(resolved_at = now)。"""
        try:
            self.conn.execute(
                "UPDATE node_conflicts SET resolved_at=? "
                "WHERE env_id=? AND resolved_at IS NULL",
                (_now_iso(), env_id),
            )
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="CONFLICT_SAVE_FAILED", message=str(e)))