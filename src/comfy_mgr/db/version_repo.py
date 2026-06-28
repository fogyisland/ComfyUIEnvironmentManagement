"""VersionRepo:version_history 表 CRUD。"""
from __future__ import annotations
import sqlite3
from typing import TypedDict
from comfy_mgr.result import Result, ServiceError


class VersionRecordDict(TypedDict, total=False):
    id: str
    env_id: str
    package: str
    action: str
    version_before: str | None
    version_after: str | None
    result: str
    error_message: str | None
    performed_at: str


class VersionRepo:
    def __init__(self, conn: sqlite3.Connection):
        self.conn = conn

    def insert(self, rec: VersionRecordDict) -> Result[None]:
        try:
            self.conn.execute("""
                INSERT INTO version_history
                    (id, env_id, package, action, version_before,
                     version_after, result, error_message, performed_at)
                VALUES (:id, :env_id, :package, :action, :version_before,
                        :version_after, :result, :error_message, :performed_at)
            """, rec)
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="VERSION_SAVE_FAILED", message=str(e)))

    def get(self, record_id: str) -> VersionRecordDict | None:
        row = self.conn.execute(
            "SELECT * FROM version_history WHERE id=?", (record_id,)
        ).fetchone()
        return dict(row) if row else None

    def list_by_env(self, env_id: str, *, limit: int | None = None) -> list[VersionRecordDict]:
        sql = "SELECT * FROM version_history WHERE env_id=? ORDER BY performed_at DESC"
        params: tuple = (env_id,)
        if limit is not None:
            sql += " LIMIT ?"
            params = (env_id, limit)
        rows = self.conn.execute(sql, params).fetchall()
        return [dict(r) for r in rows]

    def list_by_env_and_package(
        self, env_id: str, package: str, *, limit: int = 50,
    ) -> list[VersionRecordDict]:
        rows = self.conn.execute(
            "SELECT * FROM version_history WHERE env_id=? AND package=? "
            "ORDER BY performed_at DESC LIMIT ?",
            (env_id, package, limit),
        ).fetchall()
        return [dict(r) for r in rows]
