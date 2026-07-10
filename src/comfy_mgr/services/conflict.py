"""ConflictService:节点冲突检测,听 nodesChanged 自动重算。"""
from __future__ import annotations
import json
import sqlite3
import uuid
from collections import defaultdict
from typing import Literal

from comfy_mgr.db.conflict_repo import ConflictRepo
from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.infra.pkg_meta import _now_iso
from comfy_mgr.models.conflict import Conflict
from comfy_mgr.result import Result, ServiceError


ConflictType = Literal["duplicate_class", "version_mismatch", "missing_dep"]


class ConflictService:
    """M2 spec §6:duplicate_class 检测 + 自动重算。

    - detect(env_id) 读 enabled 节点 → 构建 class→nodes map →
      任意 class >1 nodes 产生一条 duplicate_class Conflict。
    - 听 EventBus 的 nodesChanged,自动重新 detect(env_id)。
    - resolve / ignore 委托给 ConflictRepo。
    """

    def __init__(
        self,
        conn: sqlite3.Connection,
        bus: EventBus,
        node_service=None,  # ScannedNodeService(duck type,避免循环 import);默认 None
    ):
        self.conn = conn
        self.node_service = node_service
        self.bus = bus
        self.repo = ConflictRepo(conn)
        # 注册监听
        bus.on("nodesChanged", self._on_nodes_changed)

    def _on_nodes_changed(self, env_id: str) -> None:
        """nodesChanged 事件 → 同步重算当前 env。"""
        self.detect(env_id)

    # ---------------- detect ----------------

    def detect(self, env_id: str) -> Result[list[Conflict]]:
        """扫 enabled 节点 → 构建冲突列表 → 清旧写新。

        返回 Result.ok([...]) 即使列表为空也算 ok。
        """
        try:
            # 1) 读 enabled 节点
            rows = self.conn.execute(
                "SELECT * FROM scanned_nodes "
                "WHERE env_id=? AND status='enabled'",
                (env_id,),
            ).fetchall()

            # 2) duplicate_class: class → [node_id, ...]
            class_to_nodes: dict[str, list[str]] = defaultdict(list)
            for row in rows:
                for cls in json.loads(row["class_mappings"] or "[]"):
                    class_to_nodes[cls].append(row["id"])

            conflicts: list[Conflict] = []
            for cls, ids in class_to_nodes.items():
                if len(ids) > 1:
                    conflicts.append(Conflict(
                        id=f"cf-{uuid.uuid4().hex[:8]}",
                        env_id=env_id,
                        conflict_type="duplicate_class",
                        node_ids=sorted(ids),
                        detail={"class": cls},
                        detected_at=_now_iso(),
                    ))

            # 3) 清旧活跃 + 写新
            clear_r = self.repo.resolve_active(env_id)
            if not clear_r.ok:
                return Result.fail(clear_r.error)
            for c in conflicts:
                ins_r = self.repo.insert(c)
                if not ins_r.ok:
                    return Result.fail(ins_r.error)

            return Result.ok(conflicts)
        except Exception as e:
            return Result.fail(ServiceError(
                code="CONFLICT_DETECT_FAILED", message=str(e)))

    # ---------------- query / state mutation ----------------

    def list_active(self, env_id: str) -> Result[list[Conflict]]:
        """返回本 env 当前活跃冲突(未解决、未忽略)。

        M2 review Important #3 修复:之前签名是 `list[Conflict]`,DB 抛异常
        会直接到 QML → Python AttributeError,破坏项目 Result 约定。改成
        Result[list[Conflict]];NodeBridge.conflictList 走 _invoke 风格
        解析 envelope,QML 拿到统一 {"ok": bool, "value"|"error": ...}。
        """
        try:
            rows = self.repo.list_active(env_id)
            return Result.ok(rows)
        except Exception as e:
            return Result.fail(ServiceError(
                code="CONFLICT_LIST_FAILED", message=str(e)))

    def resolve(self, conflict_id: str) -> Result[None]:
        """标记一条冲突已解决(resolved_at = now)。"""
        return self.repo.resolve(conflict_id)

    def ignore(self, conflict_id: str) -> Result[None]:
        """标记一条冲突被忽略(ignored=1,resolved_at = now)。"""
        return self.repo.ignore(conflict_id)
