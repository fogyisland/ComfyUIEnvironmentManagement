"""CatalogBridge：节点注册表 CRUD（无 PySide6 依赖）。"""
from __future__ import annotations
from comfy_mgr.infra.event_bus import EventBus
from app.bridge.base import BaseBridge
from comfy_mgr.services.catalog import CatalogService


class CatalogBridge(BaseBridge):

    def __init__(self, service: CatalogService, bus: EventBus):
        super().__init__(bus)
        self._service = service
        bus.on("nodeListChanged", lambda: self.bus.emit("ws.push", "nodeListChanged"))

    def list_nodes(self) -> dict:
        nodes = self._service.list_nodes()
        return {"ok": True, "value": [
            {"id": n.id, "url": n.repo_url, "name": n.name} for n in nodes
        ]}

    def add_node(self, url: str) -> dict:
        result = self._service.add_node(url)
        if not result.ok:
            self.bus.emit("ws.push", "errorOccurred", result.error.code, result.error.message)
        else:
            self.bus.emit("ws.push", "nodeListChanged")
        return {"ok": result.ok,
                "value": {"id": result.value.id} if result.ok else None,
                "error": {"code": result.error.code, "message": result.error.message} if not result.ok else None}

    def remove_node(self, node_id: str) -> dict:
        result = self._service.remove_node(node_id)
        if not result.ok:
            self.bus.emit("ws.push", "errorOccurred", result.error.code, result.error.message)
        else:
            self.bus.emit("ws.push", "nodeListChanged")
        return {"ok": result.ok,
                "value": None,
                "error": {"code": result.error.code, "message": result.error.message} if not result.ok else None}