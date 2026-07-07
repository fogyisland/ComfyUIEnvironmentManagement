"""SettingsBridge：暴露 SettingsService（无 PySide6 依赖）。"""
from __future__ import annotations
from comfy_mgr.infra.event_bus import EventBus
from app.bridge.base import BaseBridge
from comfy_mgr.settings import SettingsService


class SettingsBridge(BaseBridge):

    def __init__(self, service: SettingsService, bus: EventBus):
        super().__init__(bus)
        self._service = service

    @property
    def current(self) -> dict:
        return {
            "catalog_db_path": self._service.get("catalog_db_path"),
            "theme": self._service.get("theme"),
            "theme_mode": self._service.get("theme_mode"),
            "language": self._service.get("language"),
            "log_level": self._service.get("log_level"),
            "node_disable_mode": self._service.get("node_disable_mode"),
            "default_python_path": self._service.get("default_python_path"),
            "catalog_auto_refresh": self._service.get("catalog_auto_refresh"),
            "catalog_auto_refresh_minutes": self._service.get("catalog_auto_refresh_minutes"),
            "compat_api_base_url": self._service.get("compat_api_base_url"),
        }

    def set_value(self, key: str, value) -> dict:
        try:
            self._service.set(key, value)
        except Exception as e:
            self.bus.emit("ws.push", "errorOccurred", "SET_FAILED", str(e))
            return {"ok": False, "error": {"code": "SET_FAILED", "message": str(e)}}
        self.bus.emit("ws.push", "settingsChanged", key)
        return {"ok": True, "value": None}

    def get_all(self) -> dict:
        return {"ok": True, "value": self.current}

    def migrate_db_path(self, new_path: str) -> dict:
        from pathlib import Path
        return self._invoke(self._service.migrate_db_path, Path(new_path))

    def reload(self) -> dict:
        try:
            self._service._load()  # noqa: SLF001
            self.bus.emit("ws.push", "settingsChanged", "*")
            return {"ok": True, "value": None}
        except Exception as e:
            return {"ok": False, "error": {"code": "RELOAD_FAILED", "message": str(e)}}
