import json
import pytest
from pathlib import Path
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.models.environment import Environment, EnvironmentRepo, PORT_BASE

@pytest.fixture
def repo(tmp_path):
    db = tmp_path / "test.db"
    conn = get_connection(db)
    init_schema(conn)
    return EnvironmentRepo(conn)

def make_env(**overrides):
    defaults = dict(
        id="env-abc",
        name="env1",
        root_path=Path("D:/envs/env1"),
        comfyui_layout="shared",
        comfyui_source=Path("D:/shared/ComfyUI"),
        venv_path=Path("D:/envs/env1/venv"),
        python_executable=Path("D:/envs/env1/venv/Scripts/python.exe"),
        custom_nodes_path=Path("D:/envs/env1/custom_nodes"),
        extra_model_paths_yaml=Path("D:/envs/env1/extra_model_paths.yaml"),
        port=8188,
        enabled_node_ids=["node-x"],
        status="stopped",
        pid=None,
    )
    defaults.update(overrides)
    return Environment(**defaults)

def test_port_base_is_8188():
    assert PORT_BASE == 8188

def test_save_and_get_roundtrip(repo):
    env = make_env()
    assert repo.save(env).ok
    loaded = repo.get("env-abc")
    assert loaded is not None
    assert loaded.name == "env1"
    assert loaded.port == 8188
    assert loaded.enabled_node_ids == ["node-x"]

def test_get_returns_none_if_missing(repo):
    assert repo.get("nope") is None

def test_list_all_returns_all(repo):
    repo.save(make_env(id="e1", name="e1"))
    repo.save(make_env(id="e2", name="e2"))
    all_envs = repo.list_all()
    assert len(all_envs) == 2
    assert {e.id for e in all_envs} == {"e1", "e2"}

def test_delete_removes(repo):
    repo.save(make_env())
    assert repo.delete("env-abc").ok
    assert repo.get("env-abc") is None

def test_delete_missing_returns_fail(repo):
    result = repo.delete("nope")
    assert not result.ok
    assert result.error.code == "ENV_NOT_FOUND"

def test_save_updates_existing(repo):
    env = make_env()
    repo.save(env)
    env.port = 8190
    repo.save(env)
    assert repo.get("env-abc").port == 8190