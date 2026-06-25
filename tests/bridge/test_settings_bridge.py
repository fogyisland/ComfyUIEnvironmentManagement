"""SettingsBridge 测试。"""
from pathlib import Path
from app.bridge.settings_bridge import SettingsBridge
from comfy_mgr.settings import SettingsService


def test_current_returns_all_settings(qapp, tmp_appdata):
    svc = SettingsService()
    bridge = SettingsBridge(svc)
    cur = bridge.current
    assert cur["theme"] == "material_purple"
    assert cur["theme_mode"] == "system"
    assert cur["language"] == "zh_CN"


def test_setValue_persists_and_emits_signal(qapp, tmp_appdata, qtbot):
    svc = SettingsService()
    bridge = SettingsBridge(svc)
    with qtbot.waitSignal(bridge.settingsChanged, timeout=1000) as blocker:
        result = bridge.setValue("theme_mode", "dark")
    assert result["ok"]
    assert blocker.args == ["theme_mode"]
    assert svc.get("theme_mode") == "dark"


def test_setValue_theme_mode_emits_themeModeChanged(qapp, tmp_appdata, qtbot):
    svc = SettingsService()
    bridge = SettingsBridge(svc)
    with qtbot.waitSignal(bridge.themeModeChanged, timeout=1000) as blocker:
        bridge.setValue("theme_mode", "dark")
    assert blocker.args == ["dark"]


def test_migrateDbPath_copies_file(qapp, tmp_appdata, qtbot):
    import json
    svc = SettingsService()
    # 写一个 fake db
    old_db = svc.resolve_db_path()
    old_db.parent.mkdir(parents=True, exist_ok=True)
    old_db.write_text("FAKE", encoding="utf-8")
    bridge = SettingsBridge(svc)
    new_path = Path(tmp_appdata) / "new_catalog.db"
    result = bridge.migrateDbPath(str(new_path))
    assert result["ok"]
    assert new_path.exists()