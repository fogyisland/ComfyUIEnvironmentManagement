"""NodeBridge 测试 (M0/M1 + M2 扩展 slot)。"""
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
    mock_deps["conflict"].list_active.return_value = [c]
    result = bridge.conflictList("e1")
    assert len(result) == 1
    assert result[0]["id"] == "cf-1"


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
    )
