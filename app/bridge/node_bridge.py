"""NodeBridge：把 NodeService 暴露给 QML（M1 仅透传 enable/disable，M2 加冲突检测）。"""
from __future__ import annotations
from PySide6.QtCore import Signal, Slot
from app.bridge.base import BaseBridge
from comfy_mgr.services.node import NodeService


class NodeBridge(BaseBridge):
    nodeEnabled = Signal(str, str)   # env_id, node_id
    nodeDisabled = Signal(str, str)

    def __init__(self, service: NodeService):
        super().__init__()
        self._service = service

    @Slot(str, str, result="QVariant")
    def enableInEnv(self, env_id: str, node_id: str) -> dict:
        result = self._invoke(self._service.enable_in_env, env_id, node_id)
        if result["ok"]:
            self.nodeEnabled.emit(env_id, node_id)
        return result

    @Slot(str, str, result="QVariant")
    def disableInEnv(self, env_id: str, node_id: str) -> dict:
        result = self._invoke(self._service.disable_in_env, env_id, node_id)
        if result["ok"]:
            self.nodeDisabled.emit(env_id, node_id)
        return result
