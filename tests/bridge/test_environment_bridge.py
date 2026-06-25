"""EnvironmentBridge 测试。"""
from pathlib import Path
from unittest.mock import MagicMock
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


def test_envList_returns_dicts(qapp):
    mock_svc = MagicMock()
    mock_svc.list_all.return_value = [_fake_env()]
    bridge = EnvironmentBridge(mock_svc)
    result = bridge.envList
    assert len(result) == 1
    assert result[0]["name"] == "test"
    assert result[0]["port"] == 8188
    assert result[0]["comfyuiLayout"] == "shared"


def test_createEnv_returns_ok_and_emits_envCreated(qapp, qtbot):
    mock_svc = MagicMock()
    mock_svc.create.return_value = Result.ok(_fake_env("e2", "newname"))
    bridge = EnvironmentBridge(mock_svc)
    with qtbot.waitSignal(bridge.envCreated, timeout=1000) as blocker:
        result = bridge.createEnv(
            "newname", "shared", "C:/python.exe", "C:/ComfyUI", 8200
        )
    assert result["ok"]
    assert result["value"].name == "newname"
    assert blocker.args == ["e2"]
    mock_svc.create.assert_called_once()


def test_createEnv_emits_envListChanged(qapp, qtbot):
    mock_svc = MagicMock()
    mock_svc.create.return_value = Result.ok(_fake_env())
    bridge = EnvironmentBridge(mock_svc)
    with qtbot.waitSignal(bridge.envListChanged, timeout=1000):
        bridge.createEnv("x", "shared", "C:/python.exe", "C:/ComfyUI", 8188)


def test_deleteEnv_emits_envDeleted_and_envListChanged(qapp, qtbot):
    mock_svc = MagicMock()
    mock_svc.delete.return_value = Result.ok(None)
    bridge = EnvironmentBridge(mock_svc)
    with qtbot.waitSignal(bridge.envDeleted, timeout=1000) as del_blocker, \
         qtbot.waitSignal(bridge.envListChanged, timeout=1000):
        result = bridge.deleteEnv("e1", False)
    assert result["ok"]
    assert del_blocker.args == ["e1"]


def test_cloneEnv_emits_envCreated(qapp, qtbot):
    mock_svc = MagicMock()
    mock_svc.clone.return_value = Result.ok(_fake_env("e-new", "clone"))
    bridge = EnvironmentBridge(mock_svc)
    with qtbot.waitSignal(bridge.envCreated, timeout=1000) as blocker:
        result = bridge.cloneEnv("e1", "clone")
    assert result["ok"]
    assert blocker.args == ["e-new"]


def test_createEnv_emits_error_on_failure(qapp, qtbot):
    mock_svc = MagicMock()
    mock_svc.create.return_value = Result.fail(
        ServiceError("ENV_NAME_DUPLICATE", "环境名已存在"))
    bridge = EnvironmentBridge(mock_svc)
    with qtbot.waitSignal(bridge.errorOccurred, timeout=1000) as blocker:
        result = bridge.createEnv("dup", "shared", "C:/python.exe", "C:/ComfyUI", 8188)
    assert not result["ok"]
    assert result["error"]["code"] == "ENV_NAME_DUPLICATE"
    assert blocker.args == ["ENV_NAME_DUPLICATE", "环境名已存在"]
