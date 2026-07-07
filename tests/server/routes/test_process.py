"""process route tests."""
from __future__ import annotations
import pytest
from fastapi.testclient import TestClient
from unittest.mock import MagicMock


@pytest.fixture
def process_client(mock_process_bridge):
    from comfy_mgr.server.app import build_app
    ctx = MagicMock()
    ctx.process._state_repo.list_all.return_value = []
    ctx.environment.list_all.return_value = []
    ctx.environment.get.return_value = None
    app = build_app(ctx)
    app.state.process_bridge = mock_process_bridge
    return TestClient(app), mock_process_bridge


def test_start_env_ok(process_client):
    client, bridge = process_client
    bridge.start_env.return_value = {"ok": True, "value": {"pid": 1234, "port": 8188}}
    r = client.post("/api/v1/process/start-env", json={"env_id": "e1"})
    assert r.status_code == 200
    assert r.json()["value"]["pid"] == 1234


def test_stop_env_passes_timeout(process_client):
    client, bridge = process_client
    bridge.stop_env.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/process/stop-env", json={"env_id": "e1", "timeout": 5.0})
    assert r.status_code == 200
    bridge.stop_env.assert_called_once_with(env_id="e1", timeout=5.0)


def test_get_status_ok(process_client):
    client, bridge = process_client
    bridge.get_status.return_value = {"ok": True, "value": {"running": True, "pid": 42, "port": 8188}}
    r = client.post("/api/v1/process/get-status", json={"env_id": "e1"})
    assert r.status_code == 200
    assert r.json()["value"]["running"] is True


def test_logs_for_ok(process_client):
    client, bridge = process_client
    bridge.logs_for.return_value = ["line1", "line2"]
    r = client.post("/api/v1/process/logs-for", json={"env_id": "e1"})
    assert r.status_code == 200
    assert r.json()["value"] == ["line1", "line2"]


def test_running_envs_ok(process_client):
    client, bridge = process_client
    bridge.running_envs.return_value = ["e1", "e2"]
    r = client.post("/api/v1/process/running-envs")
    assert r.status_code == 200
    assert r.json()["value"] == ["e1", "e2"]


def test_start_env_validates_env_id_required():
    from comfy_mgr.server.app import build_app
    from unittest.mock import MagicMock
    ctx = MagicMock()
    ctx.process._state_repo.list_all.return_value = []
    ctx.environment.list_all.return_value = []
    app = build_app(ctx)
    app.state.process_bridge = MagicMock()
    client = TestClient(app)
    r = client.post("/api/v1/process/start-env", json={})
    assert r.status_code == 422
