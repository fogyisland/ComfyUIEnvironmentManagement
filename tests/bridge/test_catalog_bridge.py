"""CatalogBridge 测试 — 无 Qt。"""
from __future__ import annotations
from pathlib import Path
from unittest.mock import MagicMock
import pytest
from comfy_mgr.result import Result, ServiceError
from comfy_mgr.models.node import Node
from app.bridge.catalog_bridge import CatalogBridge


def _node(node_id="ltdrdata__ComfyUI-Impact-Pack"):
    return Node(
        id=node_id, name="ComfyUI-Impact-Pack",
        repo_url="https://github.com/ltdrdata/ComfyUI-Impact-Pack",
        local_path=Path("C:/catalog/ComfyUI-Impact-Pack"),
    )


@pytest.fixture
def bridge(mock_bus):
    mock_svc = MagicMock()
    return CatalogBridge(service=mock_svc, bus=mock_bus), mock_svc


def test_list_nodes_returns_dicts(bridge):
    b, mock_svc = bridge
    mock_svc.list_nodes.return_value = [_node()]
    result = b.list_nodes()
    assert result["ok"] is True
    assert len(result["value"]) == 1
    assert result["value"][0]["name"] == "ComfyUI-Impact-Pack"
    assert result["value"][0]["url"].startswith("https://")


def test_add_node_returns_ok_and_emits_node_list_changed(bridge, mock_bus):
    b, mock_svc = bridge
    mock_svc.add_node.return_value = Result.ok(_node())
    result = b.add_node("https://github.com/ltdrdata/ComfyUI-Impact-Pack")
    assert result["ok"] is True
    assert ("ws.push", "nodeListChanged") in mock_bus.emit_calls


def test_remove_node_emits_node_list_changed(bridge, mock_bus):
    b, mock_svc = bridge
    mock_svc.remove_node.return_value = Result.ok(None)
    result = b.remove_node("ltdrdata__ComfyUI-Impact-Pack")
    assert result["ok"] is True
    assert ("ws.push", "nodeListChanged") in mock_bus.emit_calls


def test_add_node_emits_error_on_duplicate(bridge, mock_bus):
    b, mock_svc = bridge
    mock_svc.add_node.return_value = Result.fail(
        ServiceError("NODE_ALREADY_EXISTS", "节点已存在"))
    result = b.add_node("https://github.com/ltdrdata/ComfyUI-Impact-Pack")
    assert not result["ok"]
    assert ("ws.push", "errorOccurred", "NODE_ALREADY_EXISTS", "节点已存在") in mock_bus.emit_calls