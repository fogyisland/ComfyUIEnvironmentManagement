import json
import pytest
from pathlib import Path
from comfy_mgr.settings import SettingsService, DEFAULT_SETTINGS

def test_default_settings_when_no_file(tmp_appdata):
    svc = SettingsService()
    assert svc.get("catalog_db_path") == DEFAULT_SETTINGS["catalog_db_path"]
    assert svc.get("theme") == "material_purple"
    assert svc.get("language") == "zh_CN"

def test_settings_persists_to_file(tmp_appdata):
    svc = SettingsService()
    svc.set("language", "en_US")
    # 重新读取
    svc2 = SettingsService()
    assert svc2.get("language") == "en_US"

def test_settings_file_location(tmp_appdata):
    SettingsService()
    expected = tmp_appdata / "ComfyUI-Manager" / "settings.json"
    assert expected.exists()

def test_get_unknown_key_returns_none(tmp_appdata):
    svc = SettingsService()
    assert svc.get("nonexistent") is None

def test_set_creates_file_if_missing(tmp_appdata):
    svc = SettingsService()
    svc.set("log_level", "DEBUG")
    svc.save()
    data = json.loads((tmp_appdata / "ComfyUI-Manager" / "settings.json").read_text())
    assert data["log_level"] == "DEBUG"
