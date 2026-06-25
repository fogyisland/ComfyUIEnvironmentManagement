"""SettingsBridge：暴露 SettingsService 给 QML。"""
from __future__ import annotations
from PySide6.QtCore import Property, Signal, Slot
from app.bridge.base import BaseBridge
from comfy_mgr.settings import SettingsService


class SettingsBridge(BaseBridge):
    settingsChanged = Signal(str)  # key
    themeModeChanged = Signal(str)  # mode: light/dark/system

    def __init__(self, service: SettingsService):
        super().__init__()
        self._service = service

    @Property("QVariant")
    def current(self) -> dict:
        """所有当前设置（dict）。"""
        return {
            "catalog_db_path": self._service.get("catalog_db_path"),
            "theme": self._service.get("theme"),
            "theme_mode": self._service.get("theme_mode"),
            "language": self._service.get("language"),
            "log_level": self._service.get("log_level"),
            "default_python_path": self._service.get("default_python_path"),
        }

    @Slot(str, str, result="QVariant")
    def setValue(self, key: str, value: str) -> dict:
        try:
            self._service.set(key, value)
        except Exception as e:
            self.errorOccurred.emit("SET_FAILED", str(e))
            return {"ok": False, "error": {"code": "SET_FAILED", "message": str(e)}}
        self.settingsChanged.emit(key)
        if key == "theme_mode":
            self.themeModeChanged.emit(value)
        return {"ok": True, "value": None}

    @Slot(str, result="QVariant")
    def migrateDbPath(self, new_path: str) -> dict:
        from pathlib import Path
        return self._invoke(self._service.migrate_db_path, Path(new_path))

    @Slot(result="QVariant")
    def reload(self) -> dict:
        try:
            self._service._load()  # noqa: SLF001
            return {"ok": True, "value": None}
        except Exception as e:
            return {"ok": False, "error": {"code": "RELOAD_FAILED", "message": str(e)}}