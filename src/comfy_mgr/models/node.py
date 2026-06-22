from __future__ import annotations
from dataclasses import dataclass
from pathlib import Path

@dataclass
class Node:
    id: str
    name: str
    repo_url: str
    local_path: Path
    current_version: str | None = None
    description: str = ""
    author: str = ""

import sqlite3
import json
from comfy_mgr.result import Result, ServiceError

class NodeRepo:
    def __init__(self, conn: sqlite3.Connection):
        self.conn = conn

    def save(self, node: Node) -> Result[None]:
        try:
            self.conn.execute("""
                INSERT INTO nodes (id, name, repo_url, local_path, current_version, description, author)
                VALUES (?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(id) DO UPDATE SET
                    name=excluded.name,
                    repo_url=excluded.repo_url,
                    local_path=excluded.local_path,
                    current_version=excluded.current_version,
                    description=excluded.description,
                    author=excluded.author
            """, (
                node.id, node.name, node.repo_url, str(node.local_path),
                node.current_version, node.description, node.author,
            ))
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="NODE_SAVE_FAILED",
                message=str(e),
            ))

    def get(self, node_id: str) -> Node | None:
        row = self.conn.execute("SELECT * FROM nodes WHERE id = ?", (node_id,)).fetchone()
        if not row:
            return None
        return self._row_to_node(row)

    def list_all(self) -> list[Node]:
        rows = self.conn.execute("SELECT * FROM nodes ORDER BY name").fetchall()
        return [self._row_to_node(r) for r in rows]

    def delete(self, node_id: str) -> Result[None]:
        cursor = self.conn.execute("DELETE FROM nodes WHERE id = ?", (node_id,))
        if cursor.rowcount == 0:
            return Result.fail(ServiceError(
                code="NODE_NOT_FOUND",
                message=f"节点 {node_id} 不存在",
            ))
        return Result.ok(None)

    def _row_to_node(self, row) -> Node:
        d = dict(row)
        return Node(
            id=d["id"],
            name=d["name"],
            repo_url=d["repo_url"],
            local_path=Path(d["local_path"]),
            current_version=d["current_version"],
            description=d["description"] or "",
            author=d["author"] or "",
        )