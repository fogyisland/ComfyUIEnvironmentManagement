from __future__ import annotations
import json
import sqlite3
from pathlib import Path
from comfy_mgr.infra.fs import FS
from comfy_mgr.models.environment import EnvironmentRepo
from comfy_mgr.models.node import Node, NodeRepo
from comfy_mgr.result import Result, ServiceError


class NodeService:
    def __init__(self, conn: sqlite3.Connection, fs: FS, env_repo: EnvironmentRepo):
        self.conn = conn
        self.fs = fs
        self.env_repo = env_repo
        self.node_repo = NodeRepo(conn)

    def enable_in_env(self, env_id: str, node_id: str) -> Result[None]:
        env = self.env_repo.get(env_id)
        if not env:
            return Result.fail(ServiceError(
                code="ENV_NOT_FOUND",
                message=f"环境 {env_id} 不存在",
            ))
        node = self.node_repo.get(node_id)
        if not node:
            return Result.fail(ServiceError(
                code="NODE_NOT_FOUND",
                message=f"节点 {node_id} 不存在",
            ))
        link = env.custom_nodes_path / node.name
        jr = self.fs.create_junction(link, node.local_path)
        if not jr.ok:
            return jr
        if node_id not in env.enabled_node_ids:
            env.enabled_node_ids.append(node_id)
            self.env_repo.save(env)
        return Result.ok(None)

    def disable_in_env(self, env_id: str, node_id: str) -> Result[None]:
        env = self.env_repo.get(env_id)
        if not env:
            return Result.fail(ServiceError(
                code="ENV_NOT_FOUND",
                message=f"环境 {env_id} 不存在",
            ))
        node = self.node_repo.get(node_id)
        if not node:
            return Result.fail(ServiceError(
                code="NODE_NOT_FOUND",
                message=f"节点 {node_id} 不存在",
            ))
        link = env.custom_nodes_path / node.name
        self.fs.remove_junction(link)  # 不存在也不报错
        if node_id in env.enabled_node_ids:
            env.enabled_node_ids.remove(node_id)
            self.env_repo.save(env)
        return Result.ok(None)

    def list_enabled(self, env_id: str) -> list[Node]:
        env = self.env_repo.get(env_id)
        if not env:
            return []
        return [n for n in self.node_repo.list_all() if n.id in env.enabled_node_ids]
