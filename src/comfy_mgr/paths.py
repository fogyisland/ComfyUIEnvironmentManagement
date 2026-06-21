from __future__ import annotations
import os
import sys
from pathlib import Path

APP_DIR_NAME = "ComfyUI-Manager"

def get_appdata_dir() -> Path:
    """跨平台 appdata 目录。M0 仅 Windows 路径。"""
    if sys.platform == "win32":
        appdata = os.environ.get("APPDATA")
        if appdata:
            return Path(appdata) / APP_DIR_NAME
        # 兜底：尝试 USERPROFILE
        userprofile = os.environ.get("USERPROFILE")
        if userprofile:
            return Path(userprofile) / "AppData" / "Roaming" / APP_DIR_NAME
    raise RuntimeError(f"Cannot resolve appdata dir on {sys.platform}")

def get_default_db_path() -> Path:
    return get_appdata_dir() / "catalog.db"
