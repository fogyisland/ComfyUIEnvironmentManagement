"""DepRepo:dep_records 表 CRUD(UNIQUE 覆盖语义)。"""
from __future__ import annotations
import sqlite3
from typing import TypedDict
from comfy_mgr.result import Result, ServiceError


class DepRecordDict(TypedDict, total=False):
    id: str
    env_id: str
    package: str
    source: str
    dep_name: str
    dep_version_spec: str | None
    scanned_at: str


class DepRepo:
    def __init__(self, conn: sqlite3.Connection):
        self.conn = conn

    def upsert(self, rec: DepRecordDict) -> Result[None]:
        try:
            self.conn.execute("""
                INSERT INTO dep_records
                    (id, env_id, package, source, dep_name,
                     dep_version_spec, scanned_at)
                VALUES (:id, :env_id, :package, :source, :dep_name,
                        :dep_version_spec, :scanned_at)
                ON CONFLICT(env_id, package, source, dep_name) DO UPDATE SET
                    dep_version_spec=excluded.dep_version_spec,
                    scanned_at=excluded.scanned_at
            """, rec)
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="DEP_SAVE_FAILED", message=str(e)))

    def delete_by_package(self, env_id: str, package: str) -> int:
        cur = self.conn.execute(
            "DELETE FROM dep_records WHERE env_id=? AND package=?",
            (env_id, package),
        )
        return cur.rowcount

    def list_by_env(self, env_id: str) -> list[DepRecordDict]:
        rows = self.conn.execute(
            "SELECT * FROM dep_records WHERE env_id=? "
            "ORDER BY package, dep_name", (env_id,),
        ).fetchall()
        return [dict(r) for r in rows]

    def list_by_env_and_dep(self, env_id: str, dep_name: str) -> list[DepRecordDict]:
        rows = self.conn.execute(
            "SELECT * FROM dep_records WHERE env_id=? AND dep_name=?",
            (env_id, dep_name),
        ).fetchall()
        return [dict(r) for r in rows]
