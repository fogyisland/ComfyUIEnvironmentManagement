"""EnvironmentBridge：环境 CRUD 暴露给 server（无 PySide6 依赖）。"""
from __future__ import annotations
from pathlib import Path
from comfy_mgr.infra.event_bus import EventBus
from app.bridge.base import BaseBridge
from comfy_mgr.services.environment import EnvironmentService
from comfy_mgr.models.environment import Environment, PORT_BASE


def _env_to_dict(env: Environment) -> dict:
    return {
        "id": env.id, "name": env.name, "layout": env.comfyui_layout,
        "python": str(env.python_executable),
        "comfyui_source": str(env.comfyui_source) if env.comfyui_source else "",
        "port": env.port, "status": env.status, "pid": env.pid or 0,
        "root_path": str(env.root_path),
        "custom_nodes_path": str(env.custom_nodes_path),
    }


class EnvironmentBridge(BaseBridge):

    def __init__(self, service: EnvironmentService, bus: EventBus):
        super().__init__(bus)
        self._service = service
        bus.on("envListChanged", lambda: self.bus.emit("ws.push", "envListChanged"))
        bus.on("envCreated", lambda env_id: self.bus.emit("ws.push", "envCreated", env_id))
        bus.on("envDeleted", lambda env_id: self.bus.emit("ws.push", "envDeleted", env_id))
        bus.on("envCloned", lambda new_id: self.bus.emit("ws.push", "envCloned", new_id))
        bus.on("envStatusChanged", lambda env_id, status:
               self.bus.emit("ws.push", "envStatusChanged", env_id, status))

    def list_envs(self, env_id: str = "") -> dict:
        if env_id:
            env = self._service.get(env_id)
            if not env:
                return {"ok": False, "error": {"code": "ENV_NOT_FOUND",
                                                "message": f"环境 {env_id} 不存在"}}
            return {"ok": True, "value": [_env_to_dict(env)]}
        envs = self._service.list_all()
        return {"ok": True, "value": [_env_to_dict(e) for e in envs]}

    def get_env(self, env_id: str) -> dict:
        env = self._service.get(env_id)
        if not env:
            return {"ok": False, "error": {"code": "ENV_NOT_FOUND",
                                            "message": f"环境 {env_id} 不存在"}}
        return {"ok": True, "value": _env_to_dict(env)}

    def create_env(self, name: str, layout: str, python: str,
                    comfyui_source: str, port: int) -> dict:
        result = self._service.create(
            name=name,
            layout=layout,
            python_path=Path(python),
            comfyui_source=Path(comfyui_source) if comfyui_source else None,
            port=port if port else PORT_BASE,
        )
        if result.ok:
            self.bus.emit("ws.push", "envCreated", result.value.id)
            self.bus.emit("ws.push", "envListChanged")
        else:
            self.bus.emit("ws.push", "errorOccurred", result.error.code, result.error.message)
        return {"ok": result.ok,
                "value": {"env_id": result.value.id} if result.ok else None,
                "error": {"code": result.error.code, "message": result.error.message} if not result.ok else None}

    def delete_env(self, env_id: str, force: bool = False) -> dict:
        result = self._service.delete(env_id, force=force)
        if result.ok:
            self.bus.emit("ws.push", "envDeleted", env_id)
            self.bus.emit("ws.push", "envListChanged")
        else:
            self.bus.emit("ws.push", "errorOccurred", result.error.code, result.error.message)
        return {"ok": result.ok,
                "value": None,
                "error": {"code": result.error.code, "message": result.error.message} if not result.ok else None}

    def clone_env(self, src_env_id: str, new_name: str) -> dict:
        result = self._service.clone(src_env_id, new_name)
        if result.ok:
            self.bus.emit("ws.push", "envCloned", result.value.id)
            self.bus.emit("ws.push", "envListChanged")
        else:
            self.bus.emit("ws.push", "errorOccurred", result.error.code, result.error.message)
        return {"ok": result.ok,
                "value": {"env_id": result.value.id} if result.ok else None,
                "error": {"code": result.error.code, "message": result.error.message} if not result.ok else None}
