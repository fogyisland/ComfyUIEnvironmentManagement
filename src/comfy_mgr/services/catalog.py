from __future__ import annotations
import shutil
import sqlite3
from pathlib import Path
from comfy_mgr.infra.git import GitManager
from comfy_mgr.models.node import Node, NodeRepo
from comfy_mgr.result import Result, ServiceError

def derive_node_id(url: str) -> str:
    """从 GitHub URL 派生稳定 ID：owner__repo_name。"""
    # e.g. https://github.com/ltdrdata/ComfyUI-Impact-Pack → ltdrdata__ComfyUI-Impact-Pack
    parts = url.rstrip("/").rstrip(".git").split("/")
    if len(parts) >= 2:
        owner = parts[-2]
        repo = parts[-1]
        return f"{owner}__{repo}"
    return url

def derive_node_name(url: str) -> str:
    return url.rstrip("/").rstrip(".git").split("/")[-1]

class CatalogService:
    def __init__(self, conn: sqlite3.Connection, git: GitManager, catalog_root: Path):
        self.conn = conn
        self.git = git
        self.catalog_root = catalog_root
        self.repo = NodeRepo(conn)

    def add_node(self, url: str) -> Result[Node]:
        node_id = derive_node_id(url)
        if self.repo.get(node_id):
            return Result.fail(ServiceError(
                code="NODE_ALREADY_EXISTS",
                message=f"节点 {node_id} 已在 catalog 中，请用 update 更新",
            ))
        name = derive_node_name(url)
        dest = self.catalog_root / name
        clone_result = self.git.clone(url, dest)
        if not clone_result.ok:
            return Result.fail(clone_result.error)
        node = Node(
            id=node_id,
            name=name,
            repo_url=url,
            local_path=dest,
        )
        save_result = self.repo.save(node)
        if not save_result.ok:
            # 回滚：清理已 clone 的目录
            if dest.exists():
                shutil.rmtree(dest, ignore_errors=True)
            return save_result
        return Result.ok(node)

    def list_nodes(self) -> list[Node]:
        return self.repo.list_all()

    def get_node(self, node_id: str) -> Node | None:
        return self.repo.get(node_id)

    def remove_node(self, node_id: str) -> Result[None]:
        node = self.repo.get(node_id)
        if not node:
            return Result.fail(ServiceError(
                code="NODE_NOT_FOUND",
                message=f"节点 {node_id} 不存在",
            ))
        delete_result = self.repo.delete(node_id)
        if not delete_result.ok:
            return delete_result
        if node.local_path.exists():
            shutil.rmtree(node.local_path, ignore_errors=True)
        return Result.ok(None)