"""NodeBridge 测试 (M0/M1 + M2 + M3) — 无 Qt。"""
from __future__ import annotations
from unittest.mock import MagicMock
import pytest
from comfy_mgr.result import Result, ServiceError
from app.bridge.node_bridge import NodeBridge


# ============ helpers ============

def _ok(value):
    return Result.ok(value)


def _fail(code, msg):
    return Result.fail(ServiceError(code=code, message=msg))


@pytest.fixture
def bridge(mock_bus, mock_m0_service, mock_scanned_service, mock_conflict_service,
            mock_meta_service, mock_version_service, mock_dep_service,
            mock_catalog_client, mock_install_service, tmp_path):
    """NodeBridge:全服务 mock 注入。"""
    return NodeBridge(
        m0_service=mock_m0_service,
        bus=mock_bus,
        scanned_node_service=mock_scanned_service,
        conflict_service=mock_conflict_service,
        node_meta_service=mock_meta_service,
        version_service=mock_version_service,
        dep_service=mock_dep_service,
        catalog_client=mock_catalog_client,
        compat_client=MagicMock(),
        install_service=mock_install_service,
        project_root=tmp_path,
    )


# ============ M0/M1 启停 ============

def test_enable_in_env_emits_node_enabled(bridge, mock_m0_service, mock_bus):
    mock_m0_service.enable_in_env.return_value = _ok("node-1")
    result = bridge.enable_in_env("env-1", "node-1")
    assert result["ok"] is True
    assert ("ws.push", "nodeEnabled", "env-1", "node-1") in mock_bus.emit_calls


def test_disable_in_env_emits_node_disabled(bridge, mock_m0_service, mock_bus):
    mock_m0_service.disable_in_env.return_value = _ok("node-1")
    result = bridge.disable_in_env("env-1", "node-1")
    assert result["ok"] is True
    assert ("ws.push", "nodeDisabled", "env-1", "node-1") in mock_bus.emit_calls


def test_enable_in_env_emits_error_on_missing(bridge, mock_m0_service, mock_bus):
    mock_m0_service.enable_in_env.return_value = _fail("ENV_NOT_FOUND", "env1 不存在")
    result = bridge.enable_in_env("env-1", "n-1")
    assert result["ok"] is False
    assert result["error"]["code"] == "ENV_NOT_FOUND"
    assert ("ws.push", "errorOccurred", "ENV_NOT_FOUND", "env1 不存在") in mock_bus.emit_calls


# ============ M2 扫描/冲突 ============

def test_node_list_calls_scanned_service(bridge, mock_scanned_service):
    from comfy_mgr.models.scanned_node import ScannedNode
    n = ScannedNode(id="sn-1", env_id="e1", package="p1",
                    package_path="/x", class_mappings=["A"])
    mock_scanned_service.list_by_env.return_value = _ok([n])
    result = bridge.node_list("e1")
    assert result == [{
        "id": "sn-1", "env_id": "e1", "package": "p1",
        "package_path": "/x", "class_mappings": ["A"],
        "status": "enabled", "scan_meta": {},
        "version": None, "author": None,
        "description": None, "last_scanned_at": None,
    }]


def test_conflict_list_calls_conflict_service(bridge, mock_conflict_service):
    from comfy_mgr.models.conflict import Conflict
    c = Conflict(id="cf-1", env_id="e1",
                 conflict_type="duplicate_class",
                 node_ids=["sn-a", "sn-b"], detail={"class": "X"})
    mock_conflict_service.list_active.return_value = _ok([c])
    result = bridge.conflict_list("e1")
    assert len(result) == 1
    assert result[0]["id"] == "cf-1"


def test_conflict_list_returns_empty_on_error(bridge, mock_conflict_service, mock_bus):
    """list_active 返回 Result.fail 时,NodeBridge 必须 emit errorOccurred
    + 返回空列表,而不是让异常穿透到 QML。"""
    mock_conflict_service.list_active.return_value = _fail("CONFLICT_LIST_FAILED", "db boom")
    result = bridge.conflict_list("e1")
    assert result == []
    assert ("ws.push", "errorOccurred", "CONFLICT_LIST_FAILED", "db boom") in mock_bus.emit_calls


def test_request_scan_emits_node_list_changed(bridge, mock_scanned_service, mock_bus):
    mock_scanned_service.scan.return_value = _ok([])
    result = bridge.request_scan("e1")
    assert result["ok"] is True
    assert ("ws.push", "nodeListChanged") in mock_bus.emit_calls
    assert ("ws.push", "conflictListChanged") in mock_bus.emit_calls


def test_set_disabled_emits_node_list_changed(bridge, mock_scanned_service, mock_bus):
    from comfy_mgr.models.scanned_node import ScannedNode
    n = ScannedNode(id="sn-1", env_id="e1", package="p1", package_path="/x")
    mock_scanned_service.set_disabled.return_value = _ok(n)
    result = bridge.set_disabled("sn-1", True)
    assert result["ok"] is True
    assert ("ws.push", "nodeListChanged") in mock_bus.emit_calls


def test_toggle_disabled_emits_node_list_changed(bridge, mock_scanned_service, mock_bus):
    mock_scanned_service.toggle_disabled.return_value = _ok(None)
    result = bridge.toggle_disabled("sn-1")
    assert result["ok"] is True
    assert ("ws.push", "nodeListChanged") in mock_bus.emit_calls


def test_resolve_conflict_emits_conflict_list_changed(bridge, mock_conflict_service, mock_bus):
    mock_conflict_service.resolve.return_value = _ok(None)
    result = bridge.resolve_conflict("cf-1")
    assert result["ok"] is True
    assert ("ws.push", "conflictListChanged") in mock_bus.emit_calls


def test_ignore_conflict_emits_conflict_list_changed(bridge, mock_conflict_service, mock_bus):
    mock_conflict_service.ignore.return_value = _ok(None)
    result = bridge.ignore_conflict("cf-1")
    assert result["ok"] is True
    assert ("ws.push", "conflictListChanged") in mock_bus.emit_calls


def test_fetch_remote_meta_calls_meta_service(bridge, mock_meta_service):
    from comfy_mgr.models.node_meta import NodeMeta
    m = NodeMeta(package="p1", stars=10, fetched_at="2026-06-26T00:00:00")
    mock_meta_service.get_or_fetch.return_value = _ok(m)
    result = bridge.fetch_remote_meta("p1", "owner", "repo")
    assert result["ok"] is True
    mock_meta_service.get_or_fetch.assert_called_once_with("p1", "owner", "repo")


def test_get_node_detail_returns_local_plus_cached(bridge, mock_scanned_service, mock_meta_service):
    from comfy_mgr.models.scanned_node import ScannedNode
    from comfy_mgr.models.node_meta import NodeMeta
    n = ScannedNode(
        id="sn-1", env_id="e1", package="p1", package_path="/x",
        version="1.0", author="Alice", description="desc",
        class_mappings=["A"], scan_meta={"source": "ast_clean", "warnings": []},
    )
    mock_scanned_service.get.return_value = _ok(n)
    cached = NodeMeta(package="p1", stars=5, fetched_at="2026-06-26T00:00:00")
    mock_meta_service.get_cached.return_value = _ok(cached)

    result = bridge.get_node_detail("sn-1")
    assert result["ok"] is True
    assert result["value"]["local"]["version"] == "1.0"
    assert result["value"]["remote"] is not None
    assert result["value"]["remote"]["stars"] == 5


def test_m1_enable_in_env_still_works(bridge, mock_m0_service):
    """M0/M1 的 enable_in_env / disable_in_env 不能被破坏。"""
    mock_m0_service.enable_in_env.return_value = _ok(None)
    result = bridge.enable_in_env("e1", "sn-1")
    assert result["ok"] is True
    mock_m0_service.enable_in_env.assert_called_once_with("e1", "sn-1")


# ============ M3 版本管理 ============

def test_m3_upgrade_node_returns_envelope(bridge, mock_version_service):
    mock_version_service.upgrade.return_value = _ok({"id": "vh-x"})
    r = bridge.upgrade_node("env-1", "pkg-a", target=None)
    assert r["ok"] is True
    mock_version_service.upgrade.assert_called_once_with("env-1", "pkg-a")


def test_m3_upgrade_node_with_target(bridge, mock_version_service):
    mock_version_service.upgrade.return_value = _ok({})
    r = bridge.upgrade_node("env-1", "pkg-a", target="abc123")
    assert r["ok"] is True
    mock_version_service.upgrade.assert_called_once_with("env-1", "pkg-a", target="abc123")


def test_m3_upgrade_node_error_emits_error_occurred(bridge, mock_version_service, mock_bus):
    mock_version_service.upgrade.return_value = _fail("GIT_CLONE_FAILED", "not found")
    r = bridge.upgrade_node("env-1", "pkg", target=None)
    assert r["ok"] is False
    assert r["error"]["code"] == "GIT_CLONE_FAILED"
    assert ("ws.push", "errorOccurred", "GIT_CLONE_FAILED", "not found") in mock_bus.emit_calls


def test_m3_lock_version(bridge, mock_version_service):
    mock_version_service.lock.return_value = _ok({})
    r = bridge.lock_version("env-1", "pkg-a")
    assert r["ok"] is True
    mock_version_service.lock.assert_called_once_with("env-1", "pkg-a")


def test_m3_unlock_version(bridge, mock_version_service):
    mock_version_service.unlock.return_value = _ok({})
    r = bridge.unlock_version("env-1", "pkg-a")
    assert r["ok"] is True


def test_m3_rollback_version(bridge, mock_version_service):
    mock_version_service.rollback.return_value = _ok({"result": "rolled_back"})
    r = bridge.rollback_version("env-1", "pkg-a", "vh-old")
    assert r["ok"] is True
    mock_version_service.rollback.assert_called_once_with("env-1", "pkg-a", "vh-old")


def test_m3_list_version_history(bridge, mock_version_service):
    mock_version_service.list_history.return_value = _ok([])
    r = bridge.list_version_history("env-1", "pkg-a", 50)
    assert r["ok"] is True


# ============ M3 依赖 ============

def test_m3_scan_deps_emits_deps_changed(bridge, mock_dep_service, mock_bus):
    mock_dep_service.scan_deps.return_value = _ok([])
    r = bridge.scan_deps("env-1", "pkg-a")
    assert r["ok"] is True
    assert ("ws.push", "depsChanged", "env-1", "pkg-a") in mock_bus.emit_calls


def test_m3_list_deps(bridge, mock_dep_service):
    mock_dep_service.list_deps.return_value = _ok([])
    r = bridge.list_deps("env-1", "pkg-a")
    assert r["ok"] is True


def test_m3_detect_dep_conflicts(bridge, mock_dep_service):
    mock_dep_service.detect_conflicts.return_value = _ok([])
    r = bridge.detect_dep_conflicts("env-1")
    assert r["ok"] is True


def test_m3_check_global_compat_returns_not_configured(bridge, mock_dep_service):
    """compat_api_base_url 空时返回 API_NOT_CONFIGURED(透传 dep 失败)。"""
    mock_dep_service.check_global.return_value = _fail("API_NOT_CONFIGURED", "未配置")
    r = bridge.check_global_compat("env-1")
    assert r["ok"] is False
    assert r["error"]["code"] == "API_NOT_CONFIGURED"


# ============ M3 目录 ============

def test_m3_search_catalog(bridge, mock_catalog_client):
    mock_catalog_client.search_remote.return_value = _ok([])
    r = bridge.search_catalog("impact", page=1)
    assert r["ok"] is True
    mock_catalog_client.search_remote.assert_called_once_with("impact", limit=20)


def test_m3_refresh_catalog_emits_count(bridge, mock_catalog_client, mock_bus):
    mock_catalog_client.list_remote.return_value = _ok([{"id": "p1"}])
    r = bridge.refresh_catalog()
    assert r["ok"] is True
    assert r["value"] == 1
    assert ("ws.push", "catalogUpdated", 1) in mock_bus.emit_calls


def test_m3_install_from_catalog(bridge, mock_catalog_client, mock_install_service):
    entry = {"id": "p1", "repo": "https://github.com/x/y.git"}
    mock_catalog_client.get_remote.return_value = _ok(entry)
    mock_install_service.install_from_catalog.return_value = _ok(None)
    r = bridge.install_from_catalog("p1", "env-1")
    assert r["ok"] is True
    mock_install_service.install_from_catalog.assert_called_once_with("env-1", entry)


def test_m3_uninstall_node(bridge, mock_install_service):
    mock_install_service.uninstall.return_value = _ok(None)
    r = bridge.uninstall_node("env-1", "pkg-a")
    assert r["ok"] is True


def test_m3_check_git_portable_returns_unavailable(bridge):
    """M3 新 slot:返回 git 可用性(供 UI 启动时检测)。"""
    bridge._git_exe_resolver = lambda: None
    r = bridge.check_git_portable()
    assert r["ok"] is True
    assert r["value"]["available"] is False