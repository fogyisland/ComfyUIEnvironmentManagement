"""node route tests — 30 endpoints, 1 happy-path per endpoint + validation."""
from __future__ import annotations
import pytest
from fastapi.testclient import TestClient
from unittest.mock import MagicMock


# ============ fixtures ============

@pytest.fixture
def mock_node_bridge():
    return MagicMock()


@pytest.fixture
def node_client(mock_node_bridge):
    from comfy_mgr.server.app import build_app
    ctx = MagicMock()
    ctx.process._state_repo.list_all.return_value = []
    ctx.environment.list_all.return_value = []
    ctx.environment.get.return_value = None
    app = build_app(ctx)
    app.state.node_bridge = mock_node_bridge
    return TestClient(app), mock_node_bridge


@pytest.fixture
def validation_client():
    """Bare client for 422 validation tests."""
    from comfy_mgr.server.app import build_app
    ctx = MagicMock()
    ctx.process._state_repo.list_all.return_value = []
    ctx.environment.list_all.return_value = []
    ctx.environment.get.return_value = None
    app = build_app(ctx)
    app.state.node_bridge = MagicMock()
    return TestClient(app)


# ============ M0/M1 启停 ============

def test_enable_in_env_ok(node_client):
    client, bridge = node_client
    bridge.enable_in_env.return_value = {"ok": True, "value": "node-1"}
    r = client.post("/api/v1/node/enable-in-env",
                     json={"env_id": "env-1", "node_id": "node-1"})
    assert r.status_code == 200
    assert r.json()["ok"] is True
    bridge.enable_in_env.assert_called_once_with(env_id="env-1", node_id="node-1")


def test_enable_in_env_missing_field(validation_client):
    r = validation_client.post("/api/v1/node/enable-in-env", json={})
    assert r.status_code == 422


def test_disable_in_env_ok(node_client):
    client, bridge = node_client
    bridge.disable_in_env.return_value = {"ok": True, "value": "node-1"}
    r = client.post("/api/v1/node/disable-in-env",
                     json={"env_id": "env-1", "node_id": "node-1"})
    assert r.status_code == 200
    assert r.json()["ok"] is True


def test_disable_in_env_missing_field(validation_client):
    r = validation_client.post("/api/v1/node/disable-in-env", json={"env_id": "e1"})
    assert r.status_code == 422


def test_set_scanned_service_ok(node_client):
    client, bridge = node_client
    r = client.post("/api/v1/node/set-scanned-service", json={"env_id": "env-1"})
    assert r.status_code == 200
    assert r.json()["ok"] is True
    assert r.json()["value"]["env_id"] == "env-1"


# ============ M2 ============

def test_node_list_ok(node_client):
    client, bridge = node_client
    bridge.node_list.return_value = [{"id": "n1", "package": "p1"}]
    r = client.post("/api/v1/node/node-list", json={"env_id": "env-1"})
    assert r.status_code == 200
    bridge.node_list.assert_called_once_with(env_id="env-1")


def test_conflict_list_ok(node_client):
    client, bridge = node_client
    bridge.conflict_list.return_value = []
    r = client.post("/api/v1/node/conflict-list", json={"env_id": "env-1"})
    assert r.status_code == 200
    bridge.conflict_list.assert_called_once_with(env_id="env-1")


def test_request_scan_ok(node_client):
    client, bridge = node_client
    bridge.request_scan.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/node/request-scan", json={"env_id": "env-1"})
    assert r.status_code == 200
    assert r.json()["ok"] is True
    bridge.request_scan.assert_called_once_with(env_id="env-1")


def test_set_disabled_ok(node_client):
    client, bridge = node_client
    bridge.set_disabled.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/node/set-disabled",
                     json={"node_id": "node-1", "disabled": True})
    assert r.status_code == 200
    bridge.set_disabled.assert_called_once_with(node_id="node-1", disabled=True)


def test_toggle_disabled_ok(node_client):
    client, bridge = node_client
    bridge.toggle_disabled.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/node/toggle-disabled", json={"node_id": "node-1"})
    assert r.status_code == 200
    bridge.toggle_disabled.assert_called_once_with(node_id="node-1")


def test_resolve_conflict_ok(node_client):
    client, bridge = node_client
    bridge.resolve_conflict.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/node/resolve-conflict", json={"conflict_id": "c-1"})
    assert r.status_code == 200
    bridge.resolve_conflict.assert_called_once_with(conflict_id="c-1")


def test_ignore_conflict_ok(node_client):
    client, bridge = node_client
    bridge.ignore_conflict.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/node/ignore-conflict", json={"conflict_id": "c-1"})
    assert r.status_code == 200
    bridge.ignore_conflict.assert_called_once_with(conflict_id="c-1")


def test_fetch_remote_meta_ok(node_client):
    client, bridge = node_client
    bridge.fetch_remote_meta.return_value = {"ok": True, "value": {"stars": 100}}
    r = client.post("/api/v1/node/fetch-remote-meta",
                     json={"package": "p1", "owner": "octocat", "repo": "hello"})
    assert r.status_code == 200
    bridge.fetch_remote_meta.assert_called_once_with(
        package="p1", owner="octocat", repo="hello"
    )


def test_get_node_detail_ok(node_client):
    client, bridge = node_client
    bridge.get_node_detail.return_value = {"ok": True, "value": {"local": {}, "remote": None}}
    r = client.post("/api/v1/node/get-node-detail", json={"node_id": "node-1"})
    assert r.status_code == 200
    bridge.get_node_detail.assert_called_once_with(node_id="node-1")


# ============ M3 版本 ============

def test_list_versions_ok(node_client):
    client, bridge = node_client
    bridge.list_versions.return_value = {"ok": True, "value": ["1.0", "2.0"]}
    r = client.post("/api/v1/node/list-versions",
                     json={"env_id": "env-1", "package": "p1"})
    assert r.status_code == 200
    bridge.list_versions.assert_called_once_with(env_id="env-1", package="p1")


def test_upgrade_node_ok(node_client):
    client, bridge = node_client
    bridge.upgrade_node.return_value = {"ok": True, "value": "2.0"}
    r = client.post("/api/v1/node/upgrade-node",
                     json={"env_id": "env-1", "package": "p1", "target": "2.0"})
    assert r.status_code == 200
    bridge.upgrade_node.assert_called_once_with(
        env_id="env-1", package="p1", target="2.0"
    )


def test_upgrade_node_target_optional(node_client):
    client, bridge = node_client
    bridge.upgrade_node.return_value = {"ok": True, "value": "latest"}
    r = client.post("/api/v1/node/upgrade-node",
                     json={"env_id": "env-1", "package": "p1"})
    assert r.status_code == 200
    bridge.upgrade_node.assert_called_once_with(
        env_id="env-1", package="p1", target=None
    )


def test_downgrade_node_ok(node_client):
    client, bridge = node_client
    bridge.downgrade_node.return_value = {"ok": True, "value": "1.0"}
    r = client.post("/api/v1/node/downgrade-node",
                     json={"env_id": "env-1", "package": "p1", "target": "1.0"})
    assert r.status_code == 200
    bridge.downgrade_node.assert_called_once_with(
        env_id="env-1", package="p1", target="1.0"
    )


def test_lock_version_ok(node_client):
    client, bridge = node_client
    bridge.lock_version.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/node/lock-version",
                     json={"env_id": "env-1", "package": "p1"})
    assert r.status_code == 200
    bridge.lock_version.assert_called_once_with(env_id="env-1", package="p1")


def test_unlock_version_ok(node_client):
    client, bridge = node_client
    bridge.unlock_version.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/node/unlock-version",
                     json={"env_id": "env-1", "package": "p1"})
    assert r.status_code == 200
    bridge.unlock_version.assert_called_once_with(env_id="env-1", package="p1")


def test_rollback_version_ok(node_client):
    client, bridge = node_client
    bridge.rollback_version.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/node/rollback-version",
                     json={"env_id": "env-1", "package": "p1", "history_id": "h-1"})
    assert r.status_code == 200
    bridge.rollback_version.assert_called_once_with(
        env_id="env-1", package="p1", history_id="h-1"
    )


def test_list_version_history_ok(node_client):
    client, bridge = node_client
    bridge.list_version_history.return_value = {"ok": True, "value": [{"id": "h-1"}]}
    r = client.post("/api/v1/node/list-version-history",
                     json={"env_id": "env-1", "package": "p1", "limit": 10})
    assert r.status_code == 200
    bridge.list_version_history.assert_called_once_with(
        env_id="env-1", package="p1", limit=10
    )


# ============ M3 依赖 ============

def test_scan_deps_ok(node_client):
    client, bridge = node_client
    bridge.scan_deps.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/node/scan-deps",
                     json={"env_id": "env-1", "package": "p1"})
    assert r.status_code == 200
    bridge.scan_deps.assert_called_once_with(env_id="env-1", package="p1")


def test_list_deps_ok(node_client):
    client, bridge = node_client
    bridge.list_deps.return_value = {"ok": True, "value": []}
    r = client.post("/api/v1/node/list-deps",
                     json={"env_id": "env-1", "package": "p1"})
    assert r.status_code == 200
    bridge.list_deps.assert_called_once_with(env_id="env-1", package="p1")


def test_list_deps_empty_package(node_client):
    client, bridge = node_client
    bridge.list_deps.return_value = {"ok": True, "value": []}
    r = client.post("/api/v1/node/list-deps",
                     json={"env_id": "env-1", "package": None})
    assert r.status_code == 200
    bridge.list_deps.assert_called_once_with(env_id="env-1", package="")


def test_detect_dep_conflicts_ok(node_client):
    client, bridge = node_client
    bridge.detect_dep_conflicts.return_value = {"ok": True, "value": []}
    r = client.post("/api/v1/node/detect-dep-conflicts", json={"env_id": "env-1"})
    assert r.status_code == 200
    bridge.detect_dep_conflicts.assert_called_once_with(env_id="env-1")


def test_check_global_compat_ok(node_client):
    client, bridge = node_client
    bridge.check_global_compat.return_value = {"ok": True, "value": {"compat": True}}
    r = client.post("/api/v1/node/check-global-compat", json={"env_id": "env-1"})
    assert r.status_code == 200
    bridge.check_global_compat.assert_called_once_with(env_id="env-1")


# ============ M3 目录 ============

def test_search_catalog_ok(node_client):
    client, bridge = node_client
    bridge.search_catalog.return_value = {"ok": True, "value": [{"id": "c1"}]}
    r = client.post("/api/v1/node/search-catalog",
                     json={"query": "ip-adapter", "page": 1})
    assert r.status_code == 200
    bridge.search_catalog.assert_called_once_with(query="ip-adapter", page=1)


def test_search_catalog_default_page(node_client):
    client, bridge = node_client
    bridge.search_catalog.return_value = {"ok": True, "value": []}
    r = client.post("/api/v1/node/search-catalog", json={"query": "foo"})
    assert r.status_code == 200
    bridge.search_catalog.assert_called_once_with(query="foo", page=1)


def test_get_catalog_entry_ok(node_client):
    client, bridge = node_client
    bridge.get_catalog_entry.return_value = {"ok": True, "value": {"name": "x"}}
    r = client.post("/api/v1/node/get-catalog-entry",
                     json={"env_id": "env-1", "package": "p1"})
    assert r.status_code == 200
    bridge.get_catalog_entry.assert_called_once_with(package="p1")


def test_refresh_catalog_ok(node_client):
    client, bridge = node_client
    bridge.refresh_catalog.return_value = {"ok": True, "value": 4823}
    r = client.post("/api/v1/node/refresh-catalog", json={})
    assert r.status_code == 200
    assert r.json()["value"] == 4823


def test_install_from_catalog_ok(node_client):
    client, bridge = node_client
    bridge.install_from_catalog.return_value = {
        "ok": True, "value": {"installed_path": "/x"}
    }
    r = client.post("/api/v1/node/install-from-catalog",
                     json={"package": "comfyui-impact-pack", "target_env_id": "env-2"})
    assert r.status_code == 200
    assert r.json()["ok"] is True
    bridge.install_from_catalog.assert_called_once_with(
        package="comfyui-impact-pack", target_env_id="env-2"
    )


def test_install_from_catalog_missing_field(validation_client):
    r = validation_client.post("/api/v1/node/install-from-catalog", json={"package": "p1"})
    assert r.status_code == 422


def test_uninstall_node_ok(node_client):
    client, bridge = node_client
    bridge.uninstall_node.return_value = {"ok": True, "value": None}
    r = client.post("/api/v1/node/uninstall-node",
                     json={"env_id": "env-1", "package": "p1"})
    assert r.status_code == 200
    bridge.uninstall_node.assert_called_once_with(env_id="env-1", package="p1")


def test_check_git_portable_ok(node_client):
    client, bridge = node_client
    bridge.check_git_portable.return_value = {
        "ok": True, "value": {"available": True, "version": "2.42.0", "source": "portable"}
    }
    r = client.post("/api/v1/node/check-git-portable", json={})
    assert r.status_code == 200
    assert r.json()["value"]["available"] is True