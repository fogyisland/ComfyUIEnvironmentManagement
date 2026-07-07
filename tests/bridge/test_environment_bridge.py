"""EnvironmentBridge 测试 — 无 Qt。"""
from __future__ import annotations
from pathlib import Path
from unittest.mock import MagicMock
import pytest
from comfy_mgr.result import Result, ServiceError
from comfy_mgr.models.environment import Environment
from app.bridge.environment_bridge import EnvironmentBridge


def _fake_env(env_id: str = "e1", name: str = "test") -> Environment:
    return Environment(
        id=env_id,
        name=name,
        root_path=Path(f"C:/envs/{name}"),
        comfyui_layout="shared",
        comfyui_source=Path("C:/ComfyUI"),
        venv_path=Path(f"C:/envs/{name}/venv"),
        python_executable=Path(f"C:/envs/{name}/venv/Scripts/python.exe"),
        custom_nodes_path=Path(f"C:/envs/{name}/custom_nodes"),
        extra_model_paths_yaml=Path(f"C:/envs/{name}/extra_model_paths.yaml"),
        port=8188,
    )


@pytest.fixture
def bridge(mock_bus):
    """EnvironmentBridge:service + bus。"""
    mock_svc = MagicMock()
    return EnvironmentBridge(service=mock_svc, bus=mock_bus), mock_svc


def test_list_envs_returns_dicts(bridge):
    b, mock_svc = bridge
    mock_svc.list_all.return_value = [_fake_env()]
    result = b.list_envs()
    assert result["ok"] is True
    assert len(result["value"]) == 1
    assert result["value"][0]["name"] == "test"
    assert result["value"][0]["port"] == 8188
    assert result["value"][0]["layout"] == "shared"


def test_create_env_returns_ok_and_emits_env_created(bridge, mock_bus):
    b, mock_svc = bridge
    mock_svc.create.return_value = Result.ok(_fake_env("e2", "newname"))
    result = b.create_env(
        "newname", "shared", "C:/python.exe", "C:/ComfyUI", 8200
    )
    assert result["ok"] is True
    assert ("ws.push", "envCreated", "e2") in mock_bus.emit_calls
    assert ("ws.push", "envListChanged") in mock_bus.emit_calls
    mock_svc.create.assert_called_once()


def test_create_env_emits_error_on_failure(bridge, mock_bus):
    b, mock_svc = bridge
    mock_svc.create.return_value = Result.fail(
        ServiceError("ENV_NAME_DUPLICATE", "环境名已存在"))
    result = b.create_env("dup", "shared", "C:/python.exe", "C:/ComfyUI", 8188)
    assert not result["ok"]
    assert result["error"]["code"] == "ENV_NAME_DUPLICATE"
    assert ("ws.push", "errorOccurred", "ENV_NAME_DUPLICATE", "环境名已存在") in mock_bus.emit_calls


def test_delete_env_emits_env_deleted_and_env_list_changed(bridge, mock_bus):
    b, mock_svc = bridge
    mock_svc.delete.return_value = Result.ok(None)
    result = b.delete_env("e1", force=False)
    assert result["ok"] is True
    assert ("ws.push", "envDeleted", "e1") in mock_bus.emit_calls
    assert ("ws.push", "envListChanged") in mock_bus.emit_calls


def test_clone_env_emits_env_cloned(bridge, mock_bus):
    b, mock_svc = bridge
    mock_svc.clone.return_value = Result.ok(_fake_env("e-new", "clone"))
    result = b.clone_env("e1", "clone")
    assert result["ok"] is True
    assert ("ws.push", "envCloned", "e-new") in mock_bus.emit_calls
    assert ("ws.push", "envListChanged") in mock_bus.emit_calls


def test_get_env_returns_dict(bridge):
    b, mock_svc = bridge
    mock_svc.get.return_value = _fake_env("e1", "test")
    result = b.get_env("e1")
    assert result["ok"] is True
    assert result["value"]["name"] == "test"


def test_get_env_missing_returns_error(bridge):
    b, mock_svc = bridge
    mock_svc.get.return_value = None
    result = b.get_env("missing")
    assert not result["ok"]
    assert result["error"]["code"] == "ENV_NOT_FOUND"