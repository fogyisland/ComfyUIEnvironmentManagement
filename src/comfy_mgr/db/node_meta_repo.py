"""NodeMetaRepo:node_meta_cache 表的 CRUD。"""
from __future__ import annotations
import sqlite3
from comfy_mgr.models.node_meta import NodeMeta
from comfy_mgr.result import Result, ServiceError


class NodeMetaRepo:
    def __init__(self, conn: sqlite3.Connection):
        self.conn = conn

    def upsert(self, meta: NodeMeta) -> Result[None]:
        try:
            r = meta.to_row()
            self.conn.execute("""
                INSERT INTO node_meta_cache
                    (package, github_url, stars, last_commit, homepage,
                     fetched_at, fetch_error)
                VALUES (:package, :github_url, :stars, :last_commit, :homepage,
                        :fetched_at, :fetch_error)
                ON CONFLICT(package) DO UPDATE SET
                    github_url=excluded.github_url,
                    stars=excluded.stars,
                    last_commit=excluded.last_commit,
                    homepage=excluded.homepage,
                    fetched_at=excluded.fetched_at,
                    fetch_error=excluded.fetch_error
            """, r)
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="META_SAVE_FAILED", message=str(e)))

    def get(self, package: str) -> NodeMeta | None:
        row = self.conn.execute(
            "SELECT * FROM node_meta_cache WHERE package=?", (package,)
        ).fetchone()
        return NodeMeta.from_row(row) if row else None
