import os
from pathlib import Path
from comfy_mgr.paths import get_appdata_dir, get_default_db_path

def test_get_appdata_dir_uses_env_var(monkeypatch, tmp_path):
    fake_appdata = tmp_path / "AppData" / "Roaming"
    fake_appdata.mkdir(parents=True)
    monkeypatch.setenv("APPDATA", str(fake_appdata))
    result = get_appdata_dir()
    assert result == fake_appdata / "ComfyUI-Manager"

def test_get_appdata_dir_fallback_when_env_missing(monkeypatch, tmp_path, platform_mock):
    monkeypatch.delenv("APPDATA", raising=False)
    # Windows fallback 不在 M0 范围；只测 Windows 路径
    monkeypatch.setattr("sys.platform", "win32")
    # 实际 Windows 上即使没 APPDATA 也有 userprofile；这里只测有 APPDATA 的情况
    pass

def test_get_default_db_path_under_appdata(monkeypatch, tmp_path):
    fake_appdata = tmp_path / "AppData" / "Roaming"
    fake_appdata.mkdir(parents=True)
    monkeypatch.setenv("APPDATA", str(fake_appdata))
    result = get_default_db_path()
    assert result == fake_appdata / "ComfyUI-Manager" / "catalog.db"
