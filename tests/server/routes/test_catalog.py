"""catalog route tests."""
from __future__ import annotations
import pytest
from fastapi.testclient import TestClient
from unittest.mock import MagicMock


@pytest.fixture
def catalog_client(mock_catalog_bridge):
    from comfy_mgr.server.app import build_app
    ctx = MagicMock()
    ctx.process._state_repo.list_all.return_value = []
    ctx.environment.list_all.return_value = []
    ctx.environment.get.return_value = None
    app = build_app(ctx)
    app.state.catalog_bridge = mock_catalog_bridge
    return TestClient(app), mock_catalog_bridge


def test_list_nodes_ok(catalog_client):
    client, bridge = catalog_client
    bridge.list_nodes.return_value = {"ok": True, "value": [{"id": "n1", "url": "u"}]}
    r = client.post("/api/v1/catalog/list")
    assert r.status_code == 200
    assert r.json()["ok"] is True
    assert r.json()["value"][0]["id"] == "n1"


def test_add_node_ok(catalog_client):
    client, bridge = catalog_client
    bridge.add_node.return_value = {"ok": True, "value": {"id": "n_new"}}
    r = client.post("/api/v1/catalog/add", json={"url": "https://github.com/x/y"})
    assert r.status_code == 200
    assert r.json()["value"]["id"] == "n_new"
    bridge.add_node.assert_called_once_with(url="https://github.com/x/y")


def test_add_node_validates_url_required():
    from comfy_mgr.server.app import build_app
    from unittest.mock import MagicMock
    ctx = MagicMock()
    ctx.process._state_repo.list_all.return_value = []
    ctx.environment.list_all.return_value = []
    app = build_app(ctx)
    app.state.catalog_bridge = MagicMock()
    client = TestClient(app)
    r = client.post("/api/v1/catalog/add", json={})
    assert r.status_code == 422


def test_remove_node_ok(catalog_client):
    client, bridge = catalog_client
    bridge.remove_node.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/catalog/remove", json={"node_id": "n1"})
    assert r.status_code == 200
    bridge.remove_node.assert_called_once_with(node_id="n1")
