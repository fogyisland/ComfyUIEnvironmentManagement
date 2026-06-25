"""CatalogBridge：把 CatalogService 暴露给 QML。"""
from __future__ import annotations
from PySide6.QtCore import Property, Signal, Slot
from app.bridge.base import BaseBridge
from comfy_mgr.services.catalog import CatalogService
from comfy_mgr.models.node import Node


def _node_to_dict(node: Node) -> dict:
    return {
        "id": node.id,
        "name": node.name,
        "repoUrl": node.repo_url,
        "localPath": str(node.local_path),
        "currentVersion": node.current_version or "",
        "description": node.description or "",
        "author": node.author or "",
    }


class CatalogBridge(BaseBridge):
    nodeAdded = Signal(str)
    nodeRemoved = Signal(str)
    nodeListChanged = Signal()

    def __init__(self, service: CatalogService):
        super().__init__()
        self._service = service

    @Property("QVariantList", notify=nodeListChanged)
    def nodeList(self) -> list[dict]:
        return [_node_to_dict(n) for n in self._service.list_nodes()]

    @Slot(str, result="QVariant")
    def addNode(self, url: str) -> dict:
        result = self._invoke(self._service.add_node, url)
        if result["ok"]:
            node = result["value"]
            self.nodeAdded.emit(node.id)
            self.nodeListChanged.emit()
        return result

    @Slot(str, result="QVariant")
    def removeNode(self, node_id: str) -> dict:
        result = self._invoke(self._service.remove_node, node_id)
        if result["ok"]:
            self.nodeRemoved.emit(node_id)
            self.nodeListChanged.emit()
        return result

    @Slot(result="QVariantList")
    def listNodes(self) -> list[dict]:
        return [_node_to_dict(n) for n in self._service.list_nodes()]
