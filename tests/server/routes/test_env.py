"""env route tests."""
from __future__ import annotations
import pytest
from fastapi.testclient import TestClient
from unittest.mock import MagicMock


@pytest.fixture
def env_client(mock_env_bridge):
    from comfy_mgr.server.app import build_app
    ctx = MagicMock()
    ctx.process._state_repo.list_all.return_value = []
    ctx.environment.list_all.return_value = []
    ctx.environment.get.return_value = None
    app = build_app(ctx)
    app.state.environment_bridge = mock_env_bridge
    return TestClient(app), mock_env_bridge


def test_list_envs_ok(env_client):
    client, bridge = env_client
    bridge.list_envs.return_value = {"ok": True, "value": []}
    r = client.post("/api/v1/env/list", json={"env_id": ""})
    assert r.status_code == 200
    assert r.json()["ok"] is True


def test_create_env_ok(env_client):
    client, bridge = env_client
    bridge.create_env.return_value = {"ok": True, "value": {"env_id": "e1"}}
    r = client.post("/api/v1/env/create", json={
        "name": "test", "layout": "shared", "python": "C:/py/python.exe",
        "comfyui_source": "", "port": 8188,
    })
    assert r.status_code == 200
    assert r.json()["ok"] is True
    assert r.json()["value"]["env_id"] == "e1"


def test_create_env_validates_required_fields():
    from comfy_mgr.server.app import build_app
    from unittest.mock import MagicMock
    ctx = MagicMock()
    ctx.process._state_repo.list_all.return_value = []
    ctx.environment.list_all.return_value = []
    app = build_app(ctx)
    app.state.environment_bridge = MagicMock()
    client = TestClient(app)
    r = client.post("/api/v1/env/create", json={"name": "x"})  # 缺 layout/python/...
    assert r.status_code == 422  # pydantic validation


def test_delete_env_passes_force_flag(env_client):
    client, bridge = env_client
    bridge.delete_env.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/env/delete", json={"env_id": "e1", "force": True})
    assert r.status_code == 200
    bridge.delete_env.assert_called_once_with(env_id="e1", force=True)


def test_clone_env_ok(env_client):
    client, bridge = env_client
    bridge.clone_env.return_value = {"ok": True, "value": {"env_id": "e2"}}
    r = client.post("/api/v1/env/clone", json={"src_env_id": "e1", "new_name": "copy"})
    assert r.status_code == 200
    assert r.json()["value"]["env_id"] == "e2"
