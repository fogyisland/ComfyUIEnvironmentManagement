"""NodeBridge：M0 节点 catalog + M1 启停 + M2 扫描/冲突/详情。

历史:
  M0: enable_in_env / disable_in_env (junction, catalog 模式)
  M1: 透传 M0 NodeService 给 QML
  M2: 加 scanned_node / conflict / meta slot
"""
from __future__ import annotations
from PySide6.QtCore import Signal, Slot, Property
from app.bridge.base import BaseBridge
from comfy_mgr.models.scanned_node import ScannedNode
from comfy_mgr.models.conflict import Conflict
from comfy_mgr.models.node_meta import NodeMeta


def _scanned_to_dict(n: ScannedNode) -> dict:
    return {
        "id": n.id, "env_id": n.env_id, "package": n.package,
        "package_path": str(n.package_path), "version": n.version,
        "author": n.author, "description": n.description,
        "class_mappings": n.class_mappings, "status": n.status,
        "scan_meta": n.scan_meta, "last_scanned_at": n.last_scanned_at,
    }


def _conflict_to_dict(c: Conflict) -> dict:
    return {
        "id": c.id, "env_id": c.env_id, "conflict_type": c.conflict_type,
        "node_ids": c.node_ids, "detail": c.detail,
        "detected_at": c.detected_at, "resolved_at": c.resolved_at,
        "ignored": c.ignored,
    }


def _meta_to_dict(m: NodeMeta) -> dict:
    return {
        "package": m.package, "github_url": m.github_url,
        "stars": m.stars, "last_commit": m.last_commit,
        "homepage": m.homepage, "fetched_at": m.fetched_at,
        "fetch_error": m.fetch_error,
    }


class NodeBridge(BaseBridge):
    # M0/M1 既有
    nodeEnabled = Signal(str, str)   # env_id, node_id
    nodeDisabled = Signal(str, str)

    # M2 新增
    nodeListChanged = Signal()
    conflictListChanged = Signal()
    busyChanged = Signal()

    def __init__(
        self,
        m0_service,                       # M0 NodeService
        scanned_node_service,             # M2 per-env ScannedNodeService instance
        conflict_service,                 # M2 ConflictService
        node_meta_service,                # M2 NodeMetaService
        bus,                              # M2 EventBus
    ):
        super().__init__()
        self.m0_service = m0_service
        self.scanned = scanned_node_service
        self.conflict = conflict_service
        self.meta = node_meta_service
        self.bus = bus
        self._busy = False

        # 订阅 EventBus：其他 service 发 nodesChanged 时同步通知 QML
        bus.on("nodesChanged", lambda env_id: (
            self.nodeListChanged.emit(), self.conflictListChanged.emit()))

    # ============ M0/M1 既有 slot (不动) ============

    @Slot("QVariant")
    def setScannedService(self, scanned_node_service) -> None:
        """注入 per-env ScannedNodeService 实例。

        M2 review Critical 修复:之前 self.scanned 在 AppContext 构造时
        传 None,没有任何 caller 设置,导致 requestScan / nodeList /
        conflictList / setDisabled / toggleDisabled / getNodeDetail 全部
        AttributeError。QML 端 EnvironmentDetailPanel.Component.onCompleted
        在切到当前 env 时调本方法:
            appContext.node_bridge.setScannedService(
                appContext.scanned_node_service(currentEnvId))
        """
        self.scanned = scanned_node_service

    @Slot(str, str, result="QVariant")
    def enableInEnv(self, env_id: str, node_id: str) -> dict:
        result = self._invoke(self.m0_service.enable_in_env, env_id, node_id)
        if result["ok"]:
            self.nodeEnabled.emit(env_id, node_id)
        return result

    @Slot(str, str, result="QVariant")
    def disableInEnv(self, env_id: str, node_id: str) -> dict:
        result = self._invoke(self.m0_service.disable_in_env, env_id, node_id)
        if result["ok"]:
            self.nodeDisabled.emit(env_id, node_id)
        return result

    # ============ M2 新增 slot ============

    @Slot(str, result="QVariantList")
    def nodeList(self, env_id: str) -> list:
        r = self.scanned.list_by_env()
        if not r.ok:
            return []
        return [_scanned_to_dict(n) for n in r.value]

    @Slot(str, result="QVariantList")
    def conflictList(self, env_id: str) -> list:
        """返回本 env 当前活跃冲突列表(dict 数组)。

        M2 review Important #3 修复:ConflictService.list_active 已
        改为 Result[list[Conflict]]。这里解析 envelope,失败时 emit
        errorOccurred + 返回空列表,跟 nodeList 行为保持一致。
        """
        r = self._invoke(self.conflict.list_active, env_id)
        if not r["ok"]:
            return []
        return [_conflict_to_dict(c) for c in r["value"]]

    @Slot(str, result="QVariantMap")
    def requestScan(self, env_id: str) -> dict:
        self._set_busy(True)
        try:
            r = self._invoke(self.scanned.scan)
            if r["ok"]:
                self.nodeListChanged.emit()
                self.conflictListChanged.emit()
            return r
        finally:
            self._set_busy(False)

    @Slot(str, bool, result="QVariantMap")
    def setDisabled(self, node_id: str, disabled: bool) -> dict:
        r = self._invoke(self.scanned.set_disabled, node_id, disabled)
        if r["ok"]:
            self.nodeListChanged.emit()
        return r

    @Slot(str, result="QVariantMap")
    def toggleDisabled(self, node_id: str) -> dict:
        r = self._invoke(self.scanned.toggle_disabled, node_id)
        if r["ok"]:
            self.nodeListChanged.emit()
        return r

    @Slot(str, result="QVariantMap")
    def resolveConflict(self, conflict_id: str) -> dict:
        r = self._invoke(self.conflict.resolve, conflict_id)
        if r["ok"]:
            self.conflictListChanged.emit()
        return r

    @Slot(str, result="QVariantMap")
    def ignoreConflict(self, conflict_id: str) -> dict:
        r = self._invoke(self.conflict.ignore, conflict_id)
        if r["ok"]:
            self.conflictListChanged.emit()
        return r

    @Slot(str, str, str, result="QVariantMap")
    def fetchRemoteMeta(self, package: str, owner: str, repo: str) -> dict:
        return self._invoke(self.meta.get_or_fetch, package, owner, repo)

    @Slot(str, result="QVariantMap")
    def getNodeDetail(self, node_id: str) -> dict:
        """返回 {local: {...}, remote: {...}|None}"""
        r = self._invoke(self.scanned.get, node_id)
        if not r["ok"]:
            return r
        local = _scanned_to_dict(r["value"])
        cached = self.meta.get_cached(local["package"])
        remote = None
        if cached.ok and cached.value:
            remote = _meta_to_dict(cached.value)
        return {"ok": True, "value": {"local": local, "remote": remote}}

    # ============ busy Property ============

    def _get_busy(self) -> bool:
        return self._busy

    def _set_busy(self, val: bool) -> None:
        if self._busy != val:
            self._busy = val
            self.busyChanged.emit()

    busy = Property(bool, _get_busy, notify=busyChanged)
