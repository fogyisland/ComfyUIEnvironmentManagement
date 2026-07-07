"""torch route tests."""
from __future__ import annotations
import pytest
from fastapi.testclient import TestClient
from unittest.mock import MagicMock


@pytest.fixture
def torch_client(mock_torch_bridge):
    from comfy_mgr.server.app import build_app
    ctx = MagicMock()
    ctx.process._state_repo.list_all.return_value = []
    ctx.environment.list_all.return_value = []
    ctx.environment.get.return_value = None
    app = build_app(ctx)
    app.state.torch_bridge = mock_torch_bridge
    return TestClient(app), mock_torch_bridge


def test_detect_cuda_ok(torch_client):
    client, bridge = torch_client
    bridge.detect_cuda.return_value = {"ok": True, "value": {"cuda_version": "12.4"}}
    r = client.post("/api/v1/torch/detect-cuda")
    assert r.status_code == 200
    assert r.json()["value"]["cuda_version"] == "12.4"


def test_init_env_torch_ok(torch_client):
    client, bridge = torch_client
    bridge.init_env_torch.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/torch/init-env-torch", json={"env_id": "e1", "cu_version": "cu124"})
    assert r.status_code == 200
    bridge.init_env_torch.assert_called_once_with(env_id="e1", cu_version="cu124")


def test_init_env_torch_validates_env_id_required():
    from comfy_mgr.server.app import build_app
    from unittest.mock import MagicMock
    ctx = MagicMock()
    ctx.process._state_repo.list_all.return_value = []
    ctx.environment.list_all.return_value = []
    app = build_app(ctx)
    app.state.torch_bridge = MagicMock()
    client = TestClient(app)
    r = client.post("/api/v1/torch/init-env-torch", json={})
    assert r.status_code == 422


def test_suggested_cu_versions_ok(torch_client):
    client, bridge = torch_client
    bridge.suggested_cu_versions = ["cu118", "cu121", "cu124", "cpu"]
    r = client.get("/api/v1/torch/suggested-cu-versions")
    assert r.status_code == 200
    assert r.json()["value"] == ["cu118", "cu121", "cu124", "cpu"]
