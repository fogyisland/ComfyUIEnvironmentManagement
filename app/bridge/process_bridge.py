"""ProcessBridge：把 ProcessService 暴露给 QML。"""
from __future__ import annotations
from collections import defaultdict, deque
from PySide6.QtCore import Property, Signal, Slot
from app.bridge.base import BaseBridge
from comfy_mgr.infra.process import ProcessService
from comfy_mgr.models.environment import Environment


class ProcessBridge(BaseBridge):
    processStarted = Signal(str, int, int)  # env_id, pid, port
    processStopped = Signal(str)  # env_id
    processLogLine = Signal(str, str)  # env_id, line

    def __init__(self, service: ProcessService):
        super().__init__()
        self._service = service
        self._logs: dict[str, deque] = defaultdict(lambda: deque(maxlen=500))

    @Slot(str, result="QVariant")
    def startEnv(self, env_id: str) -> dict:
        env = self._find_env(env_id)
        if not env:
            return {"ok": False, "error": {"code": "ENV_NOT_FOUND", "message": "环境不存在"}}
        result = self._invoke(self._service.start, env)
        if result["ok"]:
            handle = result["value"]
            self.processStarted.emit(env_id, handle.pid, handle.port)
        return result

    @Slot(str, float, result="QVariant")
    def stopEnv(self, env_id: str, timeout: float = 10.0) -> dict:
        env = self._find_env(env_id)
        if not env:
            return {"ok": False, "error": {"code": "ENV_NOT_FOUND", "message": "环境不存在"}}
        result = self._invoke(self._service.stop, env, timeout)
        if result["ok"]:
            self.processStopped.emit(env_id)
        return result

    @Slot(str, result="QVariant")
    def getStatus(self, env_id: str) -> dict:
        env = self._find_env(env_id)
        if not env:
            return {"ok": False, "error": {"code": "ENV_NOT_FOUND", "message": "环境不存在"}}
        status = self._service.get_status(env)
        return {
            "ok": True,
            "value": {
                "running": status.running,
                "pid": status.pid or 0,
                "port": status.port or 0,
            },
        }

    @Property("QVariantList", notify=processLogLine)
    def logLines(self) -> list[str]:
        """所有环境的最近日志（按时间倒序合并，供简单展示用）。"""
        all_lines = []
        for dq in self._logs.values():
            all_lines.extend(dq)
        return all_lines[-200:]

    @Slot(str, result="QVariantList")
    def logsFor(self, env_id: str) -> list[str]:
        return list(self._logs.get(env_id, []))

    def _on_line(self, env_id: str, line: str) -> None:
        """ProcessService → Bridge 推 Signal。"""
        self._logs[env_id].append(line)
        self.processLogLine.emit(env_id, line)

    def _find_env(self, env_id: str) -> Environment | None:
        # ProcessService 自身没有 list，需要通过外部注入；这里走回调
        return self._env_resolver(env_id) if self._env_resolver else None

    def set_env_resolver(self, resolver) -> None:
        """AppContext 注入：根据 env_id 拿 Environment。"""
        self._env_resolver = resolver
