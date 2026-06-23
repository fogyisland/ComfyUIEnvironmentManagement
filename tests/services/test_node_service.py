from pathlib import Path
from unittest.mock import MagicMock
import pytest
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.infra.fs import FS
from comfy_mgr.infra.venv import VenvManager
from comfy_mgr.models.node import Node
from comfy_mgr.models.environment import EnvironmentRepo
from comfy_mgr.services.environment import EnvironmentService
from comfy_mgr.services.catalog import CatalogService
from comfy_mgr.infra.git import GitManager
from comfy_mgr.services.node import NodeService
from comfy_mgr.result import Result

@pytest.fixture
def setup(tmp_path):
    db = tmp_path / "test.db"
    conn = get_connection(db)
    init_schema(conn)
    project_root = tmp_path / "project"
    project_root.mkdir()
    catalog_root = project_root / "catalog" / "nodes"
    catalog_root.mkdir(parents=True)

    # 创建 catalog 假节点
    node_dir = catalog_root / "ComfyUI-X"
    node_dir.mkdir()
    (node_dir / "x.txt").write_text("x")

    # 注入 Node（绕过 git clone）
    from comfy_mgr.models.node import NodeRepo
    node_repo = NodeRepo(conn)
    node_repo.save(Node(
        id="owner__ComfyUI-X",
        name="ComfyUI-X",
        repo_url="https://github.com/owner/ComfyUI-X",
        local_path=node_dir,
    ))

    # 创建环境
    comfyui_src = tmp_path / "shared" / "ComfyUI"
    comfyui_src.mkdir(parents=True)
    (comfyui_src / "main.py").write_text("x")
    fs = FS()
    venv = MagicMock(spec=VenvManager)

    # 关键：用 tmp_path-based fake python.exe
    def _fake_venv_create(python_exe, venv_path):
        venv_path.mkdir(parents=True, exist_ok=True)
        (venv_path / "Scripts").mkdir(parents=True, exist_ok=True)
        (venv_path / "Scripts" / "python.exe").write_text("")
        return Result.ok(None)
    venv.create.side_effect = _fake_venv_create

    fake_python = tmp_path / "fake_python" / "python.exe"
    fake_python.parent.mkdir(parents=True)
    fake_python.write_text("")

    env_svc = EnvironmentService(conn, project_root, fs, venv)
    env_svc.create("e1", "shared", fake_python, comfyui_src)
    env = env_svc.list_all()[0]

    node_svc = NodeService(conn=conn, fs=fs, env_repo=EnvironmentRepo(conn))
    return node_svc, env, catalog_root

def test_enable_creates_junction_in_env(setup):
    node_svc, env, _ = setup
    assert node_svc.enable_in_env(env.id, "owner__ComfyUI-X").ok
    link = env.custom_nodes_path / "ComfyUI-X"
    assert link.exists()

def test_enable_updates_env_enabled_node_ids(setup):
    node_svc, env, _ = setup
    node_svc.enable_in_env(env.id, "owner__ComfyUI-X")
    updated = node_svc.env_repo.get(env.id)
    assert "owner__ComfyUI-X" in updated.enabled_node_ids

def test_enable_fails_if_node_missing(setup):
    node_svc, env, _ = setup
    result = node_svc.enable_in_env(env.id, "nope")
    assert not result.ok
    assert result.error.code == "NODE_NOT_FOUND"

def test_enable_fails_if_env_missing(setup):
    node_svc, _, _ = setup
    result = node_svc.enable_in_env("nope", "owner__ComfyUI-X")
    assert not result.ok
    assert result.error.code == "ENV_NOT_FOUND"

def test_disable_removes_link_and_id(setup):
    node_svc, env, _ = setup
    node_svc.enable_in_env(env.id, "owner__ComfyUI-X")
    assert node_svc.disable_in_env(env.id, "owner__ComfyUI-X").ok
    link = env.custom_nodes_path / "ComfyUI-X"
    assert not link.exists()
    assert "owner__ComfyUI-X" not in node_svc.env_repo.get(env.id).enabled_node_ids

def test_list_enabled_returns_enabled(setup):
    node_svc, env, _ = setup
    node_svc.enable_in_env(env.id, "owner__ComfyUI-X")
    enabled = node_svc.list_enabled(env.id)
    assert len(enabled) == 1
    assert enabled[0].id == "owner__ComfyUI-X"
