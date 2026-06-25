"""CatalogBridge 测试。"""
from pathlib import Path
from unittest.mock import MagicMock
from comfy_mgr.result import Result, ServiceError
from comfy_mgr.models.node import Node
from app.bridge.catalog_bridge import CatalogBridge


def _node(node_id="ltdrdata__ComfyUI-Impact-Pack"):
    return Node(
        id=node_id, name="ComfyUI-Impact-Pack",
        repo_url="https://github.com/ltdrdata/ComfyUI-Impact-Pack",
        local_path=Path("C:/catalog/ComfyUI-Impact-Pack"),
    )


def test_nodeList_returns_dicts(qapp):
    mock_svc = MagicMock()
    mock_svc.list_nodes.return_value = [_node()]
    bridge = CatalogBridge(mock_svc)
    result = bridge.nodeList
    assert len(result) == 1
    assert result[0]["name"] == "ComfyUI-Impact-Pack"
    assert result[0]["repoUrl"].startswith("https://")


def test_addNode_returns_ok_and_emits_nodeAdded(qapp, qtbot):
    mock_svc = MagicMock()
    mock_svc.add_node.return_value = Result.ok(_node())
    bridge = CatalogBridge(mock_svc)
    with qtbot.waitSignal(bridge.nodeAdded, timeout=1000) as blocker, \
         qtbot.waitSignal(bridge.nodeListChanged, timeout=1000):
        result = bridge.addNode("https://github.com/ltdrdata/ComfyUI-Impact-Pack")
    assert result["ok"]
    assert blocker.args == ["ltdrdata__ComfyUI-Impact-Pack"]


def test_removeNode_emits_nodeRemoved(qapp, qtbot):
    mock_svc = MagicMock()
    mock_svc.remove_node.return_value = Result.ok(None)
    bridge = CatalogBridge(mock_svc)
    with qtbot.waitSignal(bridge.nodeRemoved, timeout=1000) as blocker, \
         qtbot.waitSignal(bridge.nodeListChanged, timeout=1000):
        result = bridge.removeNode("ltdrdata__ComfyUI-Impact-Pack")
    assert result["ok"]
    assert blocker.args == ["ltdrdata__ComfyUI-Impact-Pack"]


def test_addNode_emits_error_on_duplicate(qapp, qtbot):
    mock_svc = MagicMock()
    mock_svc.add_node.return_value = Result.fail(
        ServiceError("NODE_ALREADY_EXISTS", "节点已存在"))
    bridge = CatalogBridge(mock_svc)
    with qtbot.waitSignal(bridge.errorOccurred, timeout=1000) as blocker:
        result = bridge.addNode("https://github.com/ltdrdata/ComfyUI-Impact-Pack")
    assert not result["ok"]
    assert blocker.args == ["NODE_ALREADY_EXISTS", "节点已存在"]
