"""NodeBridge：M0/M1/M2/M3 节点操作的中枢（无 PySide6 依赖）。"""
from __future__ import annotations
from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.infra.git_portable import default_git_resolver
from comfy_mgr.models.scanned_node import ScannedNode
from comfy_mgr.models.conflict import Conflict
from comfy_mgr.models.node_meta import NodeMeta
from app.bridge.base import BaseBridge


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
    """无 PySide6。所有事件通过 bus.emit("ws.push", channel, *args) 发。"""

    def __init__(
        self, m0_service, bus: EventBus,
        scanned_node_service=None,
        conflict_service=None,
        node_meta_service=None,
        version_service=None, dep_service=None,
        catalog_client=None, compat_client=None, install_service=None,
        project_root=None,
    ):
        super().__init__(bus)
        self.m0_service = m0_service
        self.scanned = scanned_node_service
        self.conflict = conflict_service
        self.meta = node_meta_service
        self.version = version_service
        self.dep = dep_service
        self.catalog = catalog_client
        self.compat = compat_client
        self.install = install_service
        self._project_root = project_root
        self._git_exe_resolver = None
        self._busy = False

        # 订阅 bus 事件 → 转发到 ws.push(由 WSBroadcaster 推送)
        bus.on("nodesChanged", lambda env_id: (
            self.bus.emit("ws.push", "nodeListChanged"),
            self.bus.emit("ws.push", "conflictListChanged"),
        ))
        bus.on("versionChanged", lambda env_id, package:
               self.bus.emit("ws.push", "versionChanged", env_id, package))
        bus.on("depsChanged", lambda env_id, package:
               self.bus.emit("ws.push", "depsChanged", env_id, package))
        bus.on("nodeInstalled", lambda env_id, package:
               self.bus.emit("ws.push", "nodeListChanged"))
        bus.on("nodeUninstalled", lambda env_id, package:
               self.bus.emit("ws.push", "nodeListChanged"))

    # ============ M0/M1 启停 ============

    def set_scanned_service(self, scanned_node_service) -> None:
        """注入 per-env ScannedNodeService 实例(QML 端调用)。M4 由 service factory 调用。"""
        self.scanned = scanned_node_service

    def enable_in_env(self, env_id: str, node_id: str) -> dict:
        result = self._invoke(self.m0_service.enable_in_env, env_id, node_id)
        if result["ok"]:
            self.bus.emit("ws.push", "nodeEnabled", env_id, node_id)
        return result

    def disable_in_env(self, env_id: str, node_id: str) -> dict:
        result = self._invoke(self.m0_service.disable_in_env, env_id, node_id)
        if result["ok"]:
            self.bus.emit("ws.push", "nodeDisabled", env_id, node_id)
        return result

    # ============ M2 扫描/冲突 ============

    def node_list(self, env_id: str) -> list:
        r = self.scanned.list_by_env()
        if not r.ok:
            return []
        return [_scanned_to_dict(n) for n in r.value]

    def conflict_list(self, env_id: str) -> list:
        r = self._invoke(self.conflict.list_active, env_id)
        if not r["ok"]:
            return []
        return [_conflict_to_dict(c) for c in r["value"]]

    def request_scan(self, env_id: str) -> dict:
        self._set_busy(True)
        try:
            r = self._invoke(self.scanned.scan)
            if r["ok"]:
                self.bus.emit("ws.push", "nodeListChanged")
                self.bus.emit("ws.push", "conflictListChanged")
            return r
        finally:
            self._set_busy(False)

    def set_disabled(self, node_id: str, disabled: bool) -> dict:
        r = self._invoke(self.scanned.set_disabled, node_id, disabled)
        if r["ok"]:
            self.bus.emit("ws.push", "nodeListChanged")
        return r

    def toggle_disabled(self, node_id: str) -> dict:
        r = self._invoke(self.scanned.toggle_disabled, node_id)
        if r["ok"]:
            self.bus.emit("ws.push", "nodeListChanged")
        return r

    def resolve_conflict(self, conflict_id: str) -> dict:
        r = self._invoke(self.conflict.resolve, conflict_id)
        if r["ok"]:
            self.bus.emit("ws.push", "conflictListChanged")
        return r

    def ignore_conflict(self, conflict_id: str) -> dict:
        r = self._invoke(self.conflict.ignore, conflict_id)
        if r["ok"]:
            self.bus.emit("ws.push", "conflictListChanged")
        return r

    def fetch_remote_meta(self, package: str, owner: str, repo: str) -> dict:
        return self._invoke(self.meta.get_or_fetch, package, owner, repo)

    def get_node_detail(self, node_id: str) -> dict:
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

    @property
    def busy(self) -> bool:
        return self._busy

    def _set_busy(self, val: bool) -> None:
        if self._busy != val:
            self._busy = val
            self.bus.emit("ws.push", "busyChanged")

    # ============ M3 版本管理 ============

    def list_versions(self, env_id: str, package: str) -> dict:
        return self._invoke(self.version.list_status, env_id, package)

    def upgrade_node(self, env_id: str, package: str, target=None) -> dict:
        if target:
            result = self.version.upgrade(env_id, package, target=target)
        else:
            result = self.version.upgrade(env_id, package)
        if result.ok:
            return {"ok": True, "value": getattr(result, "value", None)}
        self.bus.emit("ws.push", "errorOccurred", result.error.code, result.error.message)
        return {"ok": False, "error": {"code": result.error.code, "message": result.error.message}}

    def downgrade_node(self, env_id: str, package: str, target: str) -> dict:
        return self._invoke(self.version.downgrade, env_id, package, target)

    def lock_version(self, env_id: str, package: str) -> dict:
        return self._invoke(self.version.lock, env_id, package)

    def unlock_version(self, env_id: str, package: str) -> dict:
        return self._invoke(self.version.unlock, env_id, package)

    def rollback_version(self, env_id: str, package: str, history_id: str) -> dict:
        return self._invoke(self.version.rollback, env_id, package, history_id)

    def list_version_history(self, env_id: str, package: str, limit: int = 50) -> dict:
        result = self.version.list_history(env_id, package, limit=limit)
        if result.ok:
            return {"ok": True, "value": getattr(result, "value", None)}
        self.bus.emit("ws.push", "errorOccurred", result.error.code, result.error.message)
        return {"ok": False, "error": {"code": result.error.code, "message": result.error.message}}

    # ============ M3 依赖 ============

    def scan_deps(self, env_id: str, package: str) -> dict:
        r = self._invoke(self.dep.scan_deps, env_id, package)
        if r["ok"]:
            self.bus.emit("ws.push", "depsChanged", env_id, package)
        return r

    def list_deps(self, env_id: str, package: str = "") -> dict:
        return self._invoke(self.dep.list_deps, env_id, package if package else None)

    def detect_dep_conflicts(self, env_id: str) -> dict:
        return self._invoke(self.dep.detect_conflicts, env_id)

    def check_global_compat(self, env_id: str) -> dict:
        return self._invoke(self.dep.check_global, env_id)

    # ============ M3 目录 ============

    def search_catalog(self, query: str, page: int = 1) -> dict:
        result = self.catalog.search_remote(query, limit=20)
        if result.ok:
            return {"ok": True, "value": getattr(result, "value", None)}
        self.bus.emit("ws.push", "errorOccurred", result.error.code, result.error.message)
        return {"ok": False, "error": {"code": result.error.code, "message": result.error.message}}

    def get_catalog_entry(self, package: str) -> dict:
        return self._invoke(self.catalog.get_remote, package)

    def refresh_catalog(self) -> dict:
        result = self.catalog.list_remote(force_refresh=True)
        if result.ok:
            value = getattr(result, "value", None)
            count = len(value) if value else 0
            self.bus.emit("ws.push", "catalogUpdated", count)
            return {"ok": True, "value": count}
        self.bus.emit("ws.push", "catalogUnavailable", result.error.code)
        self.bus.emit("ws.push", "errorOccurred", result.error.code, result.error.message)
        return {"ok": False, "error": {"code": result.error.code, "message": result.error.message}}

    def install_from_catalog(self, package: str, target_env_id: str) -> dict:
        r = self._invoke(self.catalog.get_remote, package)
        if not r["ok"]:
            return r
        return self._invoke(self.install.install_from_catalog, target_env_id, r["value"])

    def uninstall_node(self, env_id: str, package: str) -> dict:
        return self._invoke(self.install.uninstall, env_id, package)

    def check_git_portable(self) -> dict:
        from comfy_mgr.infra.git_portable import git_portable_version
        git_exe = self._git_exe_resolver() if self._git_exe_resolver else None
        if git_exe is None:
            return {"ok": True, "value": {"available": False, "version": "", "source": "missing"}}
        version = ""
        if self._project_root:
            version = git_portable_version(self._project_root) or ""
        source = "portable" if "bin/git-portable" in str(git_exe) else "system"
        return {"ok": True, "value": {"available": True, "version": version, "source": source}}