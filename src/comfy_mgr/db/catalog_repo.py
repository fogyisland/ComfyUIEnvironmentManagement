"""CatalogCacheRepo:catalog_cache 表 CRUD + TTL 过滤。"""
from __future__ import annotations
import sqlite3
from datetime import datetime
from typing import TypedDict
from comfy_mgr.result import Result, ServiceError


class CatalogEntryDict(TypedDict, total=False):
    id: str
    source_url: str
    package: str
    raw_metadata: str
    cached_at: str
    expires_at: str


class CatalogCacheRepo:
    def __init__(self, conn: sqlite3.Connection):
        self.conn = conn

    def upsert(self, entry: CatalogEntryDict) -> Result[None]:
        try:
            self.conn.execute("""
                INSERT INTO catalog_cache
                    (id, source_url, package, raw_metadata,
                     cached_at, expires_at)
                VALUES (:id, :source_url, :package, :raw_metadata,
                        :cached_at, :expires_at)
                ON CONFLICT(source_url, package) DO UPDATE SET
                    raw_metadata=excluded.raw_metadata,
                    cached_at=excluded.cached_at,
                    expires_at=excluded.expires_at
            """, entry)
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="CACHE_SAVE_FAILED", message=str(e)))

    def get_by_package(self, package: str) -> CatalogEntryDict | None:
        row = self.conn.execute(
            "SELECT * FROM catalog_cache WHERE package=? LIMIT 1",
            (package,),
        ).fetchone()
        return dict(row) if row else None

    def list_all(self) -> list[CatalogEntryDict]:
        rows = self.conn.execute(
            "SELECT * FROM catalog_cache ORDER BY package"
        ).fetchall()
        return [dict(r) for r in rows]

    def delete_all_for_source(self, source_url: str) -> Result[None]:
        try:
            self.conn.execute(
                "DELETE FROM catalog_cache WHERE source_url = ?",
                (source_url,),
            )
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="CACHE_DELETE_FAILED", message=str(e)))

    def list_non_expired(self, now_iso: str | None = None) -> list[CatalogEntryDict]:
        """返回未过期的 cache 行。

        时间约定:`expires_at` 存的是 naive UTC ISO 字符串,`datetime.now()` 返回
        naive local time — 两者在调用层负责确保一致(写入层用
        `datetime.now().isoformat()` 本地,比较时 `expires_at > datetime.now().isoformat()`
        也是本地,故 naive 内部一致)。不要混合 UTC 和 local 写入。
        """
        now = now_iso or datetime.now().isoformat(timespec="seconds")
        rows = self.conn.execute(
            "SELECT * FROM catalog_cache WHERE expires_at > ? "
            "ORDER BY package", (now,),
        ).fetchall()
        return [dict(r) for r in rows]

    def search_substring(self, query: str) -> list[CatalogEntryDict]:
        escaped = query.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")
        rows = self.conn.execute(
            "SELECT * FROM catalog_cache "
            "WHERE LOWER(package) LIKE ? ESCAPE '\\' "
            "ORDER BY package",
            (f"%{escaped.lower()}%",),
        ).fetchall()
        return [dict(r) for r in rows]
