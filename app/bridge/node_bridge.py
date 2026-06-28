"""NodeBridge:M0 节点 catalog + M1 启停 + M2 扫描/冲突/详情 + M3 版本/依赖/目录。

历史:
  M0: enable_in_env / disable_in_env (junction, catalog 模式)
  M1: 透传 M0 NodeService 给 QML
  M2: scanned_node / conflict / meta slot
  M3: version / dep / catalog / install slot (本文件扩展)
"""
from __future__ import annotations
from PySide6.QtCore import Signal, Slot, Property
from app.bridge.base import BaseBridge
from comfy_mgr.models.scanned_node import ScannedNode
from comfy_mgr.models.conflict import Conflict
from comfy_mgr.models.node_meta import NodeMeta
from comfy_mgr.infra.git_portable import default_git_resolver


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

    # M3 新增
    versionChanged = Signal(str, str)            # env_id, package
    depsChanged = Signal(str, str)               # env_id, package
    catalogUpdated = Signal(int)                 # entry_count
    catalogUnavailable = Signal(str)             # reason
    installProgress = Signal(str, str, int, str) # env_id, package, percent, message

    def __init__(
        self,
        m0_service,                       # M0 NodeService
        scanned_node_service,             # M2 per-env ScannedNodeService instance
        conflict_service,                 # M2 ConflictService
        node_meta_service,                # M2 NodeMetaService
        bus,                              # M2 EventBus
        # M3 新增参数(都有默认 None,保持 M2 调用兼容)
        version_service=None,
        dep_service=None,
        catalog_client=None,
        compat_client=None,
        install_service=None,
        project_root=None,            # M3 新增,给 checkGitPortable 用
    ):
        super().__init__()
        self.m0_service = m0_service
        self.scanned = scanned_node_service
        self.conflict = conflict_service
        self.meta = node_meta_service
        self.bus = bus
        self._busy = False
        # M3
        self.version = version_service
        self.dep = dep_service
        self.catalog = catalog_client
        self.compat = compat_client
        self.install = install_service
        self._project_root = project_root
        # _git_exe_resolver 由 AppContext 在 M3 services 构造完成后注入
        # (见 app/app_context.py:_git_exe_resolver 赋值)。单一来源,
        # 避免构造期 + 后注入两层 lambda 包装。
        self._git_exe_resolver = None

        # M2 既有 EventBus 订阅
        bus.on("nodesChanged", lambda env_id: (
            self.nodeListChanged.emit(), self.conflictListChanged.emit()))
        # M3 订阅
        bus.on("versionChanged", lambda env_id, package:
               self.versionChanged.emit(env_id, package))
        bus.on("depsChanged", lambda env_id, package:
               self.depsChanged.emit(env_id, package))
        bus.on("nodeInstalled", lambda env_id, package:
               self.nodeListChanged.emit())
        bus.on("nodeUninstalled", lambda env_id, package:
               self.nodeListChanged.emit())

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

    # ============ M3 新增 slot:版本管理 ============

    @Slot(str, str, result="QVariant")
    def listVersions(self, env_id: str, package: str) -> dict:
        return self._invoke(self.version.list_status, env_id, package)

    @Slot(str, str, str, result="QVariant")
    def upgradeNode(self, env_id: str, package: str, target: str) -> dict:
        # _invoke 不支持 kwargs,这里直接调 service + envelope 构造
        kw = {"target": target} if target else {"target": None}
        result = self.version.upgrade(env_id, package, **kw)
        if result.ok:
            return {"ok": True, "value": getattr(result, "value", None)}
        msg = self._tr(result.error.message)
        self.errorOccurred.emit(result.error.code, msg)
        return {"ok": False, "error": {"code": result.error.code, "message": msg}}

    @Slot(str, str, str, result="QVariant")
    def downgradeNode(self, env_id: str, package: str, target: str) -> dict:
        return self._invoke(self.version.downgrade, env_id, package, target)

    @Slot(str, str, result="QVariant")
    def lockVersion(self, env_id: str, package: str) -> dict:
        return self._invoke(self.version.lock, env_id, package)

    @Slot(str, str, result="QVariant")
    def unlockVersion(self, env_id: str, package: str) -> dict:
        return self._invoke(self.version.unlock, env_id, package)

    @Slot(str, str, str, result="QVariant")
    def rollbackVersion(self, env_id: str, package: str,
                        history_id: str) -> dict:
        return self._invoke(self.version.rollback, env_id, package, history_id)

    @Slot(str, str, int, result="QVariant")
    def listVersionHistory(self, env_id: str, package: str,
                           limit: int = 50) -> dict:
        # _invoke 不支持 kwargs,这里直接调 service + envelope 构造
        result = self.version.list_history(env_id, package, limit=limit)
        if result.ok:
            return {"ok": True, "value": getattr(result, "value", None)}
        msg = self._tr(result.error.message)
        self.errorOccurred.emit(result.error.code, msg)
        return {"ok": False, "error": {"code": result.error.code, "message": msg}}

    # ============ M3 新增 slot:依赖 ============

    @Slot(str, str, result="QVariant")
    def scanDeps(self, env_id: str, package: str) -> dict:
        r = self._invoke(self.dep.scan_deps, env_id, package)
        if r["ok"]:
            self.depsChanged.emit(env_id, package)
        return r

    @Slot(str, str, result="QVariant")
    def listDeps(self, env_id: str, package: str) -> dict:
        return self._invoke(self.dep.list_deps, env_id,
                            package if package else None)

    @Slot(str, result="QVariant")
    def detectDepConflicts(self, env_id: str) -> dict:
        return self._invoke(self.dep.detect_conflicts, env_id)

    @Slot(str, result="QVariant")
    def checkGlobalCompat(self, env_id: str) -> dict:
        return self._invoke(self.dep.check_global, env_id)

    # ============ M3 新增 slot:目录 ============

    @Slot(str, int, result="QVariant")
    def searchCatalog(self, query: str, page: int = 1) -> dict:
        # _invoke 不支持 kwargs,这里直接调 service + envelope 构造
        # M3 简化:page 参数忽略
        result = self.catalog.search_remote(query, limit=20)
        if result.ok:
            return {"ok": True, "value": getattr(result, "value", None)}
        msg = self._tr(result.error.message)
        self.errorOccurred.emit(result.error.code, msg)
        return {"ok": False, "error": {"code": result.error.code, "message": msg}}

    @Slot(str, result="QVariant")
    def getCatalogEntry(self, package: str) -> dict:
        return self._invoke(self.catalog.get_remote, package)

    @Slot(result="QVariant")
    def refreshCatalog(self) -> dict:
        """强制刷新远程 catalog,返回 envelope `{"ok": True, "value": entry_count}` (int)。

        返回值是条目总数 (int),不是条目列表。QML 消费者如需条目内容,
        应该在 `catalogUpdated(count)` 信号触发后调用 `searchCatalog()`
        或 `getCatalogEntry(package)` 获取列表 / 单条。

        失败时:
          - 发送 `catalogUnavailable(reason_code)` 信号
          - 发送 `errorOccurred(code, message)` 信号
          - 返回 envelope `{"ok": False, "error": {code, message}}`
        """
        # _invoke 不支持 kwargs,这里直接调 service + envelope 构造
        # 返回 value=entry_count(QML 端用),信号 catalogUpdated 也携带 count。
        result = self.catalog.list_remote(force_refresh=True)
        if result.ok:
            value = getattr(result, "value", None)
            count = len(value) if value else 0
            self.catalogUpdated.emit(count)
            return {"ok": True, "value": count}
        msg = self._tr(result.error.message)
        self.catalogUnavailable.emit(result.error.code)
        self.errorOccurred.emit(result.error.code, msg)
        return {"ok": False, "error": {"code": result.error.code, "message": msg}}

    @Slot(str, str, result="QVariant")
    def installFromCatalog(self, package: str, target_env_id: str) -> dict:
        # 先查 catalog_entry,再 install_from_catalog
        r = self._invoke(self.catalog.get_remote, package)
        if not r["ok"]:
            return r
        return self._invoke(self.install.install_from_catalog,
                            target_env_id, r["value"])

    @Slot(str, str, result="QVariant")
    def uninstallNode(self, env_id: str, package: str) -> dict:
        return self._invoke(self.install.uninstall, env_id, package)

    @Slot(result="QVariant")
    def checkGitPortable(self) -> dict:
        """UI 启动时调一次,显示 git 可用性。"""
        from comfy_mgr.infra.git_portable import git_portable_version
        git_exe = self._git_exe_resolver()
        if git_exe is None:
            return {"ok": True, "value": {
                "available": False, "version": "",
                "source": "missing",
            }}
        version = ""
        if self._project_root:
            version = git_portable_version(self._project_root) or ""
        source = "portable" if "bin/git-portable" in str(git_exe) else "system"
        return {"ok": True, "value": {
            "available": True, "version": version, "source": source,
        }}
