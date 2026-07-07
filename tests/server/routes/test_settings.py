"""settings route tests."""
from __future__ import annotations
import pytest
from fastapi.testclient import TestClient
from unittest.mock import MagicMock


@pytest.fixture
def settings_client(mock_settings_bridge):
    from comfy_mgr.server.app import build_app
    ctx = MagicMock()
    ctx.process._state_repo.list_all.return_value = []
    ctx.environment.list_all.return_value = []
    ctx.environment.get.return_value = None
    app = build_app(ctx)
    app.state.settings_bridge = mock_settings_bridge
    return TestClient(app), mock_settings_bridge


def test_get_all_ok(settings_client):
    client, bridge = settings_client
    bridge.current = {"theme": "light", "language": "zh_CN"}
    r = client.post("/api/v1/settings/get-all")
    assert r.status_code == 200
    assert r.json()["ok"] is True
    assert r.json()["value"]["theme"] == "light"


def test_set_value_ok(settings_client):
    client, bridge = settings_client
    bridge.set_value.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/settings/set-value", json={"key": "theme", "value": "dark"})
    assert r.status_code == 200
    bridge.set_value.assert_called_once_with(key="theme", value="dark")


def test_set_value_validates_key_required():
    from comfy_mgr.server.app import build_app
    from unittest.mock import MagicMock
    ctx = MagicMock()
    ctx.process._state_repo.list_all.return_value = []
    ctx.environment.list_all.return_value = []
    app = build_app(ctx)
    app.state.settings_bridge = MagicMock()
    client = TestClient(app)
    r = client.post("/api/v1/settings/set-value", json={"value": "x"})
    assert r.status_code == 422


def test_migrate_db_path_ok(settings_client):
    client, bridge = settings_client
    bridge.migrate_db_path.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/settings/migrate-db-path", json={"new_path": "C:/new/db.sqlite"})
    assert r.status_code == 200
    bridge.migrate_db_path.assert_called_once_with(new_path="C:/new/db.sqlite")


def test_reload_ok(settings_client):
    client, bridge = settings_client
    bridge.reload.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/settings/reload")
    assert r.status_code == 200
    bridge.reload.assert_called_once_with()
