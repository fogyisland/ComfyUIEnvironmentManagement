"""NodeBridge 测试 (M0/M1 + M2 扩展 slot + M3 扩展)。"""
from __future__ import annotations
from unittest.mock import MagicMock
import pytest
from app.bridge.node_bridge import NodeBridge
from comfy_mgr.result import Result, ServiceError


# ============ M0/M1 回归测试 ============

def test_enableInEnv_emits_nodeEnabled(qapp, qtbot):
    bridge = _make_bridge(m0_service=MagicMock())
    bridge.m0_service.enable_in_env.return_value = Result.ok(None)
    with qtbot.waitSignal(bridge.nodeEnabled, timeout=1000) as blocker:
        result = bridge.enableInEnv("env1", "ltdrdata__ComfyUI-Impact-Pack")
    assert result["ok"]
    assert blocker.args == ["env1", "ltdrdata__ComfyUI-Impact-Pack"]


def test_disableInEnv_emits_nodeDisabled(qapp, qtbot):
    bridge = _make_bridge(m0_service=MagicMock())
    bridge.m0_service.disable_in_env.return_value = Result.ok(None)
    with qtbot.waitSignal(bridge.nodeDisabled, timeout=1000) as blocker:
        result = bridge.disableInEnv("env1", "ltdrdata__ComfyUI-Impact-Pack")
    assert result["ok"]
    assert blocker.args == ["env1", "ltdrdata__ComfyUI-Impact-Pack"]


def test_enableInEnv_emits_error_on_missing(qapp, qtbot):
    bridge = _make_bridge(m0_service=MagicMock())
    bridge.m0_service.enable_in_env.return_value = Result.fail(
        ServiceError("ENV_NOT_FOUND", "env1 不存在"))
    with qtbot.waitSignal(bridge.errorOccurred, timeout=1000) as blocker:
        result = bridge.enableInEnv("env1", "n1")
    assert not result["ok"]
    assert blocker.args == ["ENV_NOT_FOUND", "env1 不存在"]


# ============ M2 新增 slot 测试 ============

@pytest.fixture
def mock_deps():
    scanned = MagicMock()
    conflict = MagicMock()
    meta = MagicMock()
    bus = MagicMock()
    return {"scanned": scanned, "conflict": conflict, "meta": meta, "bus": bus}


@pytest.fixture
def bridge(mock_deps, qapp) -> NodeBridge:
    return _make_bridge(
        m0_service=MagicMock(),
        scanned_node_service=mock_deps["scanned"],
        conflict_service=mock_deps["conflict"],
        node_meta_service=mock_deps["meta"],
        bus=mock_deps["bus"],
    )


def test_node_list_property_calls_scanned_service(bridge, mock_deps):
    from comfy_mgr.models.scanned_node import ScannedNode
    n = ScannedNode(id="sn-1", env_id="e1", package="p1",
                    package_path="/x", class_mappings=["A"])
    mock_deps["scanned"].list_by_env.return_value = Result.ok([n])
    result = bridge.nodeList("e1")
    assert result == [{
        "id": "sn-1", "env_id": "e1", "package": "p1",
        "package_path": "/x", "class_mappings": ["A"],
        "status": "enabled", "scan_meta": {},
        "version": None, "author": None,
        "description": None, "last_scanned_at": None,
    }]


def test_conflict_list_property_calls_conflict_service(bridge, mock_deps):
    from comfy_mgr.models.conflict import Conflict
    c = Conflict(id="cf-1", env_id="e1",
                 conflict_type="duplicate_class",
                 node_ids=["sn-a", "sn-b"], detail={"class": "X"})
    mock_deps["conflict"].list_active.return_value = Result.ok([c])
    result = bridge.conflictList("e1")
    assert len(result) == 1
    assert result[0]["id"] == "cf-1"


def test_conflict_list_returns_empty_on_error(bridge, mock_deps, qtbot):
    """list_active 返回 Result.fail 时,NodeBridge 必须 emit errorOccurred
    + 返回空列表,而不是让异常穿透到 QML。"""
    mock_deps["conflict"].list_active.return_value = Result.fail(
        ServiceError("CONFLICT_LIST_FAILED", "db boom"))
    with qtbot.waitSignal(bridge.errorOccurred, timeout=1000) as blocker:
        result = bridge.conflictList("e1")
    assert result == []
    assert blocker.args == ["CONFLICT_LIST_FAILED", "db boom"]


def test_request_scan_emits_nodes_changed(bridge, mock_deps, qtbot):
    mock_deps["scanned"].scan.return_value = Result.ok([])
    with qtbot.waitSignal(bridge.nodeListChanged, timeout=1000):
        result = bridge.requestScan("e1")
    assert result["ok"] is True


def test_set_disabled_emits_nodes_changed(bridge, mock_deps, qtbot):
    from comfy_mgr.models.scanned_node import ScannedNode
    n = ScannedNode(id="sn-1", env_id="e1", package="p1", package_path="/x")
    mock_deps["scanned"].set_disabled.return_value = Result.ok(n)
    with qtbot.waitSignal(bridge.nodeListChanged, timeout=1000):
        result = bridge.setDisabled("sn-1", True)
    assert result["ok"] is True


def test_resolve_conflict_emits_conflict_changed(bridge, mock_deps, qtbot):
    mock_deps["conflict"].resolve.return_value = Result.ok(None)
    with qtbot.waitSignal(bridge.conflictListChanged, timeout=1000):
        result = bridge.resolveConflict("cf-1")
    assert result["ok"] is True


def test_ignore_conflict_emits_conflict_changed(bridge, mock_deps, qtbot):
    mock_deps["conflict"].ignore.return_value = Result.ok(None)
    with qtbot.waitSignal(bridge.conflictListChanged, timeout=1000):
        result = bridge.ignoreConflict("cf-1")
    assert result["ok"] is True


def test_fetch_remote_meta_calls_meta_service(bridge, mock_deps):
    from comfy_mgr.models.node_meta import NodeMeta
    m = NodeMeta(package="p1", stars=10, fetched_at="2026-06-26T00:00:00")
    mock_deps["meta"].get_or_fetch.return_value = Result.ok(m)
    result = bridge.fetchRemoteMeta("p1", "owner", "repo")
    assert result["ok"] is True
    mock_deps["meta"].get_or_fetch.assert_called_once_with("p1", "owner", "repo")


def test_get_node_detail_returns_local_plus_cached(bridge, mock_deps):
    from comfy_mgr.models.scanned_node import ScannedNode
    from comfy_mgr.models.node_meta import NodeMeta
    n = ScannedNode(
        id="sn-1", env_id="e1", package="p1", package_path="/x",
        version="1.0", author="Alice", description="desc",
        class_mappings=["A"], scan_meta={"source": "ast_clean", "warnings": []},
    )
    mock_deps["scanned"].get.return_value = Result.ok(n)
    cached = NodeMeta(package="p1", stars=5, fetched_at="2026-06-26T00:00:00")
    mock_deps["meta"].get_cached.return_value = Result.ok(cached)

    result = bridge.getNodeDetail("sn-1")
    assert result["ok"] is True
    assert result["value"]["local"]["version"] == "1.0"
    assert result["value"]["remote"] is not None
    assert result["value"]["remote"]["stars"] == 5


def test_m1_enableInEnv_still_works(bridge, mock_deps):
    """M0/M1 的 enableInEnv / disableInEnv 不能被破坏。"""
    bridge.m0_service.enable_in_env.return_value = Result.ok(None)
    result = bridge.enableInEnv("e1", "sn-1")
    assert result["ok"] is True
    bridge.m0_service.enable_in_env.assert_called_once_with("e1", "sn-1")


# ============ helpers ============

def _make_bridge(
    m0_service,
    scanned_node_service=None,
    conflict_service=None,
    node_meta_service=None,
    bus=None,
    version_service=None,
    dep_service=None,
    catalog_client=None,
    compat_client=None,
    install_service=None,
    project_root=None,
) -> NodeBridge:
    """构造 NodeBridge,缺省用 MagicMock 占位。"""
    from comfy_mgr.infra.event_bus import EventBus
    return NodeBridge(
        m0_service=m0_service,
        scanned_node_service=(
            scanned_node_service if scanned_node_service is not None
            else MagicMock()
        ),
        conflict_service=(
            conflict_service if conflict_service is not None
            else MagicMock()
        ),
        node_meta_service=(
            node_meta_service if node_meta_service is not None
            else MagicMock()
        ),
        bus=bus if bus is not None else EventBus(),
        version_service=version_service,
        dep_service=dep_service,
        catalog_client=catalog_client,
        compat_client=compat_client,
        install_service=install_service,
        project_root=project_root,
    )


# ============ M3 新增测试 ============

from comfy_mgr.services.version import VersionService
from comfy_mgr.services.dependency import DepService
from comfy_mgr.services.install import InstallService
from comfy_mgr.infra.catalog_http_client import CatalogHTTPClient
from comfy_mgr.infra.compat_http_client import CompatHTTPClient


@pytest.fixture
def m3_mock_deps(mocker):
    return {
        "m0": mocker.MagicMock(),
        "scanned": mocker.MagicMock(),
        "conflict": mocker.MagicMock(),
        "meta": mocker.MagicMock(),
        "version": mocker.MagicMock(spec=VersionService),
        "dep": mocker.MagicMock(spec=DepService),
        "install": mocker.MagicMock(spec=InstallService),
        "catalog": mocker.MagicMock(spec=CatalogHTTPClient),
        "compat": mocker.MagicMock(spec=CompatHTTPClient),
    }


@pytest.fixture
def m3_bridge(qapp, m3_mock_deps, mocker):
    from app.bridge.node_bridge import NodeBridge
    return NodeBridge(
        m0_service=m3_mock_deps["m0"],
        scanned_node_service=m3_mock_deps["scanned"],
        conflict_service=m3_mock_deps["conflict"],
        node_meta_service=m3_mock_deps["meta"],
        version_service=m3_mock_deps["version"],
        dep_service=m3_mock_deps["dep"],
        catalog_client=m3_mock_deps["catalog"],
        compat_client=m3_mock_deps["compat"],
        install_service=m3_mock_deps["install"],
        bus=mocker.MagicMock(),
    )


def test_m3_upgrade_node_returns_envelope(m3_bridge, m3_mock_deps):
    from comfy_mgr.result import Result
    m3_mock_deps["version"].upgrade.return_value = Result.ok({"id": "vh-x"})
    r = m3_bridge.upgradeNode("env-1", "pkg-a", "")
    assert r["ok"] is True
    m3_mock_deps["version"].upgrade.assert_called_once_with("env-1", "pkg-a", target=None)


def test_m3_upgrade_node_with_target(m3_bridge, m3_mock_deps):
    from comfy_mgr.result import Result
    m3_mock_deps["version"].upgrade.return_value = Result.ok({})
    r = m3_bridge.upgradeNode("env-1", "pkg-a", "abc123")
    assert r["ok"] is True
    m3_mock_deps["version"].upgrade.assert_called_once_with("env-1", "pkg-a", target="abc123")


def test_m3_lock_version(m3_bridge, m3_mock_deps):
    from comfy_mgr.result import Result
    m3_mock_deps["version"].lock.return_value = Result.ok({})
    r = m3_bridge.lockVersion("env-1", "pkg-a")
    assert r["ok"] is True
    m3_mock_deps["version"].lock.assert_called_once_with("env-1", "pkg-a")


def test_m3_unlock_version(m3_bridge, m3_mock_deps):
    from comfy_mgr.result import Result
    m3_mock_deps["version"].unlock.return_value = Result.ok({})
    r = m3_bridge.unlockVersion("env-1", "pkg-a")
    assert r["ok"] is True


def test_m3_rollback_version(m3_bridge, m3_mock_deps):
    from comfy_mgr.result import Result
    m3_mock_deps["version"].rollback.return_value = Result.ok({"result": "rolled_back"})
    r = m3_bridge.rollbackVersion("env-1", "pkg-a", "vh-old")
    assert r["ok"] is True
    m3_mock_deps["version"].rollback.assert_called_once_with("env-1", "pkg-a", "vh-old")


def test_m3_list_version_history(m3_bridge, m3_mock_deps):
    from comfy_mgr.result import Result
    m3_mock_deps["version"].list_history.return_value = Result.ok([])
    r = m3_bridge.listVersionHistory("env-1", "pkg-a", 50)
    assert r["ok"] is True


def test_m3_scan_deps_emits_deps_changed(m3_bridge, m3_mock_deps, qtbot):
    from comfy_mgr.result import Result
    m3_mock_deps["dep"].scan_deps.return_value = Result.ok([])
    with qtbot.waitSignal(m3_bridge.depsChanged, timeout=1000):
        r = m3_bridge.scanDeps("env-1", "pkg-a")
    assert r["ok"] is True


def test_m3_detect_dep_conflicts(m3_bridge, m3_mock_deps):
    from comfy_mgr.result import Result
    m3_mock_deps["dep"].detect_conflicts.return_value = Result.ok([])
    r = m3_bridge.detectDepConflicts("env-1")
    assert r["ok"] is True


def test_m3_check_global_compat_returns_not_configured(m3_bridge, m3_mock_deps):
    """compat_api_base_url 空时返回 API_NOT_CONFIGURED(透传 compat_client 失败)。"""
    from comfy_mgr.result import Result, ServiceError
    m3_mock_deps["dep"].check_global.return_value = Result.fail(ServiceError(
        code="API_NOT_CONFIGURED", message="未配置"))
    r = m3_bridge.checkGlobalCompat("env-1")
    assert r["ok"] is False
    assert r["error"]["code"] == "API_NOT_CONFIGURED"


def test_m3_search_catalog(m3_bridge, m3_mock_deps):
    from comfy_mgr.result import Result
    m3_mock_deps["catalog"].search_remote.return_value = Result.ok([])
    r = m3_bridge.searchCatalog("impact", 1)
    assert r["ok"] is True
    m3_mock_deps["catalog"].search_remote.assert_called_once_with("impact", limit=20)


def test_m3_refresh_catalog_emits_signal(m3_bridge, m3_mock_deps, qtbot):
    from comfy_mgr.result import Result
    m3_mock_deps["catalog"].list_remote.return_value = Result.ok([{"id": "p1"}])
    with qtbot.waitSignal(m3_bridge.catalogUpdated, timeout=1000):
        r = m3_bridge.refreshCatalog()
    assert r["ok"] is True
    assert r["value"] == 1


def test_m3_install_from_catalog(m3_bridge, m3_mock_deps):
    from comfy_mgr.result import Result
    entry = {"id": "p1", "repo": "https://github.com/x/y.git"}
    m3_mock_deps["catalog"].get_remote.return_value = Result.ok(entry)
    m3_mock_deps["install"].install_from_catalog.return_value = Result.ok(None)
    r = m3_bridge.installFromCatalog("p1", "env-1")
    assert r["ok"] is True
    m3_mock_deps["install"].install_from_catalog.assert_called_once_with("env-1", entry)


def test_m3_uninstall_node(m3_bridge, m3_mock_deps):
    from comfy_mgr.result import Result
    m3_mock_deps["install"].uninstall.return_value = Result.ok(None)
    r = m3_bridge.uninstallNode("env-1", "pkg-a")
    assert r["ok"] is True


def test_m3_check_git_portable(m3_bridge, m3_mock_deps):
    """M3 新 slot:返回 git 可用性(供 UI 启动时检测)。"""
    m3_bridge._git_exe_resolver = lambda: None
    r = m3_bridge.checkGitPortable()
    assert r["ok"] is True
    assert r["value"]["available"] is False
