"""ProcessBridge：环境进程启停 + 日志流（无 PySide6 依赖）。"""
from __future__ import annotations
from collections import defaultdict, deque
from comfy_mgr.infra.event_bus import EventBus
from app.bridge.base import BaseBridge
from comfy_mgr.infra.process import ProcessService
from comfy_mgr.models.environment import Environment


class ProcessBridge(BaseBridge):

    def __init__(self, service: ProcessService, bus: EventBus):
        super().__init__(bus)
        self._service = service
        self._logs: dict[str, deque] = defaultdict(lambda: deque(maxlen=500))
        self._log_version = 0
        self._env_resolver = None

    @property
    def log_version(self) -> int:
        return self._log_version

    @property
    def log_lines(self) -> list:
        all_lines = []
        for dq in self._logs.values():
            all_lines.extend(dq)
        return all_lines[-200:]

    def start_env(self, env_id: str) -> dict:
        env = self._find_env(env_id)
        if not env:
            return {"ok": False, "error": {"code": "ENV_NOT_FOUND", "message": "环境不存在"}}
        result = self._invoke(self._service.start, env)
        if result["ok"]:
            handle = result["value"]
            self.bus.emit("ws.push", "envStarted", env_id, handle.pid, handle.port)
        return result

    def stop_env(self, env_id: str, timeout: float = 10.0) -> dict:
        env = self._find_env(env_id)
        if not env:
            return {"ok": False, "error": {"code": "ENV_NOT_FOUND", "message": "环境不存在"}}
        result = self._invoke(self._service.stop, env, timeout)
        if result["ok"]:
            self.bus.emit("ws.push", "envStopped", env_id)
        return result

    def get_status(self, env_id: str) -> dict:
        env = self._find_env(env_id)
        if not env:
            return {"ok": False, "error": {"code": "ENV_NOT_FOUND", "message": "环境不存在"}}
        status = self._service.get_status(env)
        return {"ok": True, "value": {
            "running": status.running, "pid": status.pid or 0, "port": status.port or 0,
        }}

    def logs_for(self, env_id: str) -> list:
        return list(self._logs.get(env_id, []))

    def running_envs(self) -> list:
        return [s.env_id for s in self._service._state_repo.list_all()]

    def _on_line(self, env_id: str, line: str) -> None:
        """ProcessService → bridge 推 ws.push。"""
        self._logs[env_id].append(line)
        self._log_version += 1
        self.bus.emit("ws.push", "logLine", env_id, line)

    def _find_env(self, env_id: str) -> Environment | None:
        return self._env_resolver(env_id) if self._env_resolver else None

    def set_env_resolver(self, resolver) -> None:
        self._env_resolver = resolver