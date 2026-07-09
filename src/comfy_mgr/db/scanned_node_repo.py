"""ScannedNodeRepo：scanned_nodes 表的 CRUD。"""
from __future__ import annotations
import sqlite3
from comfy_mgr.models.scanned_node import ScannedNode
from comfy_mgr.result import Result, ServiceError


class ScannedNodeRepo:
    def __init__(self, conn: sqlite3.Connection):
        self.conn = conn

    def upsert(self, node: ScannedNode) -> Result[None]:
        try:
            r = node.to_row()
            # Defensive defaults: older to_row() mocks may not include M4 fields.
            r.setdefault("locked", 0)
            r.setdefault("disable_mode", "db_flag")
            self.conn.execute("""
                INSERT INTO scanned_nodes
                    (id, env_id, package, package_path, version, author,
                     description, class_mappings, status, locked, disable_mode,
                     scan_meta, last_scanned_at)
                VALUES (:id, :env_id, :package, :package_path, :version, :author,
                        :description, :class_mappings, :status, :locked, :disable_mode,
                        :scan_meta, :last_scanned_at)
                ON CONFLICT(env_id, package) DO UPDATE SET
                    package_path=excluded.package_path,
                    version=excluded.version,
                    author=excluded.author,
                    description=excluded.description,
                    class_mappings=excluded.class_mappings,
                    status=excluded.status,
                    locked=excluded.locked,
                    disable_mode=excluded.disable_mode,
                    scan_meta=excluded.scan_meta,
                    last_scanned_at=excluded.last_scanned_at
            """, r)
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="NODE_SAVE_FAILED", message=str(e)))

    def get(self, node_id: str) -> ScannedNode | None:
        row = self.conn.execute(
            "SELECT *, locked FROM scanned_nodes WHERE id=?", (node_id,)
        ).fetchone()
        return ScannedNode.from_row(row) if row else None

    def get_by_env_package(self, env_id: str, package: str) -> ScannedNode | None:
        row = self.conn.execute(
            "SELECT * FROM scanned_nodes WHERE env_id=? AND package=?",
            (env_id, package),
        ).fetchone()
        return ScannedNode.from_row(row) if row else None

    def list_by_env(self, env_id: str, *,
                    include_disabled: bool = True) -> list[ScannedNode]:
        if include_disabled:
            rows = self.conn.execute(
                "SELECT *, locked FROM scanned_nodes WHERE env_id=? ORDER BY package",
                (env_id,),
            ).fetchall()
        else:
            rows = self.conn.execute(
                "SELECT *, locked FROM scanned_nodes WHERE env_id=? "
                "AND status='enabled' ORDER BY package",
                (env_id,),
            ).fetchall()
        return [ScannedNode.from_row(r) for r in rows]

    def list_enabled(self, env_id: str) -> list[ScannedNode]:
        return self.list_by_env(env_id, include_disabled=False)

    def set_status(self, node_id: str, status: str) -> Result[None]:
        try:
            self.conn.execute(
                "UPDATE scanned_nodes SET status=? WHERE id=?",
                (status, node_id),
            )
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="NODE_SAVE_FAILED", message=str(e)))

    def update_package_path(self, node_id: str, new_path: str) -> Result[None]:
        try:
            self.conn.execute(
                "UPDATE scanned_nodes SET package_path=? WHERE id=?",
                (new_path, node_id),
            )
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="NODE_SAVE_FAILED", message=str(e)))

    def count(self) -> int:
        row = self.conn.execute("SELECT COUNT(*) FROM scanned_nodes").fetchone()
        return row[0]
