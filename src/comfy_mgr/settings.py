from __future__ import annotations
import json
from pathlib import Path
from typing import Any
from comfy_mgr.paths import get_appdata_dir

DEFAULT_SETTINGS = {
    "catalog_db_path": None,  # None = 使用 get_default_db_path()
    "theme": "material_purple",
    "language": "zh_CN",
    "log_level": "INFO",
    "default_python_path": None,
}

class SettingsService:
    def __init__(self, path: Path | None = None):
        self.path = path or (get_appdata_dir() / "settings.json")
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._data = self._load()
        # Always ensure the file exists after construction
        if not self.path.exists():
            self.save()

    def _load(self) -> dict:
        if not self.path.exists():
            return dict(DEFAULT_SETTINGS)
        try:
            with self.path.open("r", encoding="utf-8") as f:
                loaded = json.load(f)
            # 合并默认值（处理新增字段）
            merged = dict(DEFAULT_SETTINGS)
            merged.update(loaded)
            return merged
        except json.JSONDecodeError:
            return dict(DEFAULT_SETTINGS)

    def get(self, key: str) -> Any:
        return self._data.get(key)

    def set(self, key: str, value: Any) -> None:
        self._data[key] = value
        self.save()

    def save(self) -> None:
        with self.path.open("w", encoding="utf-8") as f:
            json.dump(self._data, f, indent=2, ensure_ascii=False)

    def resolve_db_path(self) -> Path:
        """返回实际 catalog.db 路径（处理默认 vs 自定义）。"""
        configured = self._data.get("catalog_db_path")
        if configured:
            return Path(configured)
        from comfy_mgr.paths import get_default_db_path
        return get_default_db_path()
