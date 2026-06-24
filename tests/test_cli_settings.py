import json
import pytest
from pathlib import Path
from typer.testing import CliRunner
from comfy_mgr.cli import app
from comfy_mgr.paths import get_appdata_dir

runner = CliRunner()


@pytest.fixture
def isolated(tmp_path, monkeypatch):
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    project_root = tmp_path / "project"
    project_root.mkdir()
    monkeypatch.chdir(project_root)
    return tmp_path


def test_settings_show_defaults(isolated):
    result = runner.invoke(app, ["settings", "show"])
    assert result.exit_code == 0
    assert "material_purple" in result.stdout
    assert "zh_CN" in result.stdout


def test_settings_set_persists(isolated):
    runner.invoke(app, ["settings", "set", "language", "en_US"])
    settings_path = get_appdata_dir() / "settings.json"
    data = json.loads(settings_path.read_text())
    assert data["language"] == "en_US"


def test_settings_set_catalog_db_path_migrates(isolated):
    """切换 db 路径时复制旧 db 到新位置。"""
    new_path = isolated / "new_catalog.db"
    result = runner.invoke(app, [
        "settings", "set-catalog-db-path", str(new_path)
    ])
    assert result.exit_code == 0
    assert new_path.exists()

    settings_path = get_appdata_dir() / "settings.json"
    data = json.loads(settings_path.read_text())
    assert data["catalog_db_path"] == str(new_path).replace("\\", "/")