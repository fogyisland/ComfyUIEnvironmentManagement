"""EnvironmentBridge：把 EnvironmentService 暴露给 QML。"""
from __future__ import annotations
from pathlib import Path
from typing import Any
from PySide6.QtCore import Property, Signal, Slot
from app.bridge.base import BaseBridge
from comfy_mgr.services.environment import EnvironmentService
from comfy_mgr.models.environment import Environment


def _env_to_dict(env: Environment) -> dict:
    """Environment dataclass → QML 友好 dict。"""
    return {
        "id": env.id,
        "name": env.name,
        "rootPath": str(env.root_path),
        "comfyuiLayout": env.comfyui_layout,
        "comfyuiSource": str(env.comfyui_source) if env.comfyui_source else "",
        "venvPath": str(env.venv_path),
        "pythonExecutable": str(env.python_executable),
        "customNodesPath": str(env.custom_nodes_path),
        "port": env.port,
        "status": env.status,
        "pid": env.pid or 0,
        "enabledNodeIds": list(env.enabled_node_ids),
    }


class EnvironmentBridge(BaseBridge):
    envCreated = Signal(str)   # env_id
    envDeleted = Signal(str)   # env_id
    envListChanged = Signal()

    def __init__(self, service: EnvironmentService):
        super().__init__()
        self._service = service

    @Property("QVariantList", notify=envListChanged)
    def envList(self) -> list[dict]:
        return [_env_to_dict(e) for e in self._service.list_all()]

    @Slot(str, str, str, str, int, result="QVariant")
    def createEnv(
        self, name: str, layout: str, python: str,
        comfyuiSource: str, port: int = 8188,
    ) -> dict:
        from comfy_mgr.models.environment import PORT_BASE
        result = self._invoke(
            self._service.create,
            name,
            layout,
            Path(python),
            Path(comfyuiSource) if comfyuiSource else None,
            port if port else PORT_BASE,
        )
        if result["ok"]:
            env = result["value"]
            self.envCreated.emit(env.id)
            self.envListChanged.emit()
        return result

    @Slot(str, bool, result="QVariant")
    def deleteEnv(self, env_id: str, force: bool = False) -> dict:
        result = self._invoke(self._service.delete, env_id, force)
        if result["ok"]:
            self.envDeleted.emit(env_id)
            self.envListChanged.emit()
        return result

    @Slot(str, str, result="QVariant")
    def cloneEnv(self, src_env_id: str, new_name: str) -> dict:
        result = self._invoke(self._service.clone, src_env_id, new_name)
        if result["ok"]:
            env = result["value"]
            self.envCreated.emit(env.id)
            self.envListChanged.emit()
        return result

    @Slot(result="QVariantList")
    def listEnvs(self) -> list[dict]:
        return [_env_to_dict(e) for e in self._service.list_all()]

    @Slot(str, result="QVariant")
    def getEnv(self, env_id: str) -> dict | None:
        env = self._service.get(env_id)
        return _env_to_dict(env) if env else None
