"""SettingsBridge 测试 — 无 Qt。"""
from __future__ import annotations
from pathlib import Path
from unittest.mock import MagicMock
import pytest
from app.bridge.settings_bridge import SettingsBridge
from comfy_mgr.settings import SettingsService


@pytest.fixture
def bridge(mock_bus, tmp_appdata):
    """SettingsBridge:service + bus,tmp_appdata 隔离 APPDATA。"""
    svc = SettingsService()
    return SettingsBridge(service=svc, bus=mock_bus), svc


def test_current_returns_all_settings(bridge):
    b, _ = bridge
    cur = b.current
    assert cur["theme"] == "material_purple"
    assert cur["theme_mode"] == "system"
    assert cur["language"] == "zh_CN"


def test_set_value_persists_and_emits_settings_changed(bridge, mock_bus):
    b, svc = bridge
    result = b.set_value("theme_mode", "dark")
    assert result["ok"]
    assert ("ws.push", "settingsChanged", "theme_mode") in mock_bus.emit_calls
    assert svc.get("theme_mode") == "dark"


def test_set_value_emits_error_on_failure(bridge, mock_bus):
    b, svc = bridge
    # 模拟 set() 内部抛异常 → SET_FAILED
    svc.set = MagicMock(side_effect=RuntimeError("disk full"))
    result = b.set_value("theme_mode", "dark")
    assert not result["ok"]
    assert result["error"]["code"] == "SET_FAILED"
    assert ("ws.push", "errorOccurred", "SET_FAILED", "disk full") in mock_bus.emit_calls


def test_migrate_db_path_copies_file(bridge):
    import json
    b, svc = bridge
    old_db = svc.resolve_db_path()
    old_db.parent.mkdir(parents=True, exist_ok=True)
    old_db.write_text("FAKE", encoding="utf-8")
    new_path = Path(str(old_db.parent)).parent / "new_catalog.db"
    result = b.migrate_db_path(str(new_path))
    assert result["ok"]
    assert new_path.exists()


def test_reload_emits_settings_changed(bridge, mock_bus):
    b, _ = bridge
    result = b.reload()
    assert result["ok"]
    assert ("ws.push", "settingsChanged", "*") in mock_bus.emit_calls