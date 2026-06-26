"""ScannedNodeService:per-env custom_nodes 扫描 + 启停 + CRUD。"""
from __future__ import annotations
import sqlite3
import uuid
from pathlib import Path

from comfy_mgr.db.scanned_node_repo import ScannedNodeRepo
from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.infra.node_scanner import NodeScanner
from comfy_mgr.infra.pkg_meta import _parse_pyproject, _now_iso
from comfy_mgr.models.scanned_node import ScannedNode
from comfy_mgr.result import Result, ServiceError


class ScannedNodeService:
    """扫描 + 启停 + 查询。service 构造时绑定 env_id(per-env 实例),符合
    M2 spec section 6 的设计。"""

    def __init__(
        self,
        conn: sqlite3.Connection,
        env_id: str,
        scanner: NodeScanner,
        bus: EventBus,
    ):
        self.conn = conn
        self.env_id = env_id
        self.scanner = scanner
        self.bus = bus
        self.repo = ScannedNodeRepo(conn)

    # ---------------- scan ----------------

    def scan(self) -> Result[list[ScannedNode]]:
        """扫 env 的 custom_nodes/,upsert 到 nodes 表,emit nodesChanged。"""
        try:
            custom_nodes_dir = self._resolve_custom_nodes_dir()
        except ValueError as e:
            return Result.fail(ServiceError(
                code="ENV_NOT_FOUND", message=str(e)))

        # 确保 custom_nodes/ 存在
        custom_nodes_dir.mkdir(parents=True, exist_ok=True)

        pkg_dirs = [
            p for p in custom_nodes_dir.iterdir()
            if p.is_dir() and not p.name.startswith((".", "_"))
        ]

        nodes: list[ScannedNode] = []
        for pkg_dir in pkg_dirs:
            n = self._scan_one_pkg(pkg_dir)
            if n is not None:
                nodes.append(n)

        # upsert
        for n in nodes:
            r = self.repo.upsert(n)
            if not r.ok:
                return r

        # emit
        self.bus.emit("nodesChanged", self.env_id)
        return Result.ok(nodes)

    def _scan_one_pkg(self, pkg_dir: Path) -> ScannedNode:
        meta = _parse_pyproject(pkg_dir)
        init_py = pkg_dir / "__init__.py"
        classes, source, warnings = self.scanner.extract_class_mappings(init_py)
        return ScannedNode(
            id=f"sn-{uuid.uuid4().hex[:8]}",
            env_id=self.env_id,
            package=meta.name or pkg_dir.name,
            package_path=pkg_dir,
            version=meta.version,
            author=meta.author,
            description=meta.description,
            class_mappings=classes,
            status="enabled",
            scan_meta={"source": source.value, "warnings": warnings},
            last_scanned_at=_now_iso(),
        )

    def _resolve_custom_nodes_dir(self) -> Path:
        row = self.conn.execute(
            "SELECT custom_nodes_path FROM environments WHERE id=?",
            (self.env_id,),
        ).fetchone()
        if not row:
            raise ValueError(f"env {self.env_id} 不存在")
        return Path(row[0])

    # ---------------- disable / enable (db_flag mode) ----------------

    def set_disabled(self, node_id: str, disabled: bool) -> Result[ScannedNode]:
        """设置节点的 enabled/disabled。M2 阶段只实现 db_flag 模式(更新
        status 字段),folder_rename 模式留到 T16(Settings 集成)后追加。"""
        node = self.repo.get(node_id)
        if not node:
            return Result.fail(ServiceError(
                code="NODE_NOT_FOUND",
                message=f"节点 {node_id} 不存在",
            ))
        new_status = "disabled" if disabled else "enabled"

        r = self.repo.set_status(node_id, new_status)
        if not r.ok:
            return r

        updated = self.repo.get(node_id)
        if updated is None:
            return Result.fail(ServiceError(
                code="NODE_NOT_FOUND",
                message=f"节点 {node_id} 读取失败"))

        self.bus.emit("nodesChanged", self.env_id)
        return Result.ok(updated)

    def toggle_disabled(self, node_id: str) -> Result[ScannedNode]:
        """切换节点的 enabled/disabled 状态。"""
        node = self.repo.get(node_id)
        if not node:
            return Result.fail(ServiceError(
                code="NODE_NOT_FOUND",
                message=f"节点 {node_id} 不存在",
            ))
        return self.set_disabled(node_id, node.status != "disabled")
