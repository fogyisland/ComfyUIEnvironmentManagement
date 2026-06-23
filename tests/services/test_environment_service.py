from pathlib import Path
from unittest.mock import MagicMock
import pytest
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.infra.fs import FS
from comfy_mgr.infra.venv import VenvManager
from comfy_mgr.models.environment import EnvironmentRepo, PORT_BASE
from comfy_mgr.services.environment import EnvironmentService
from comfy_mgr.result import Result

@pytest.fixture
def deps(tmp_path):
    db = tmp_path / "test.db"
    conn = get_connection(db)
    init_schema(conn)
    project_root = tmp_path / "project"
    project_root.mkdir()
    fs = FS()
    venv = MagicMock(spec=VenvManager)
    venv.create.return_value = Result.ok(None)
    venv.get_python_version.return_value = Result.ok("Python 3.10.5")

    def _fake_venv_create(python_exe, venv_path):
        venv_path.mkdir(parents=True, exist_ok=True)
        (venv_path / "Scripts").mkdir(parents=True, exist_ok=True)
        (venv_path / "Scripts" / "python.exe").write_text("")
        return Result.ok(None)
    venv.create.side_effect = _fake_venv_create

    fake_python = tmp_path / "fake_python" / "python.exe"
    fake_python.parent.mkdir(parents=True)
    fake_python.write_text("")

    svc = EnvironmentService(
        conn=conn,
        project_root=project_root,
        fs=fs,
        venv=venv,
    )
    return svc, venv, project_root, conn, fake_python

def test_create_shared_env_uses_junction(deps):
    svc, venv_mock, project_root, _, fake_python = deps
    comfyui_src = project_root.parent / "shared" / "ComfyUI"
    comfyui_src.mkdir(parents=True)
    result = svc.create(
        name="env1",
        layout="shared",
        python_path=fake_python,
        comfyui_source=comfyui_src,
    )
    assert result.ok
    env = result.value
    assert env.comfyui_layout == "shared"
    assert env.comfyui_source == comfyui_src
    assert (env.root_path / "ComfyUI").exists()  # junction 或 link
    venv_mock.create.assert_called_once()
    args = venv_mock.create.call_args[0]
    assert Path(args[0]) == fake_python
    assert args[1] == env.venv_path

def test_create_independent_env_copies_comfyui(deps):
    svc, _, project_root, _, fake_python = deps
    comfyui_src = project_root.parent / "shared" / "ComfyUI"
    comfyui_src.mkdir(parents=True)
    (comfyui_src / "main.py").write_text("# comfy")
    result = svc.create(
        name="env1",
        layout="independent",
        python_path=fake_python,
        comfyui_source=comfyui_src,
    )
    assert result.ok
    env = result.value
    assert env.comfyui_layout == "independent"
    assert (env.root_path / "ComfyUI" / "main.py").read_text() == "# comfy"

def test_create_assigns_port_sequentially(deps):
    svc, _, project_root, _, fake_python = deps
    comfyui_src = project_root.parent / "shared" / "ComfyUI"
    comfyui_src.mkdir(parents=True)

    r1 = svc.create("e1", "shared", fake_python, comfyui_src)
    r2 = svc.create("e2", "shared", fake_python, comfyui_src)
    assert r1.value.port == PORT_BASE
    assert r2.value.port == PORT_BASE + 1

def test_create_fails_on_duplicate_name(deps):
    svc, _, project_root, _, fake_python = deps
    comfyui_src = project_root.parent / "shared" / "ComfyUI"
    comfyui_src.mkdir(parents=True)
    svc.create("e1", "shared", fake_python, comfyui_src)
    r2 = svc.create("e1", "shared", fake_python, comfyui_src)
    assert not r2.ok
    assert r2.error.code == "ENV_NAME_DUPLICATE"

def test_create_fails_if_python_missing(deps, tmp_path):
    svc, _, project_root, _, _ = deps
    comfyui_src = project_root.parent / "shared" / "ComfyUI"
    comfyui_src.mkdir(parents=True)
    nonexistent_python = tmp_path / "nonexistent" / "python.exe"
    result = svc.create("e1", "shared", nonexistent_python, comfyui_src)
    assert not result.ok
    assert result.error.code == "VENV_PYTHON_MISSING"

def test_create_fails_if_shared_source_missing(deps):
    svc, _, _, _, fake_python = deps
    result = svc.create("e1", "shared", fake_python, None)
    assert not result.ok
    assert result.error.code == "COMFYUI_SOURCE_MISSING"

def test_list_all_returns_created(deps):
    svc, _, project_root, _, fake_python = deps
    comfyui_src = project_root.parent / "shared" / "ComfyUI"
    comfyui_src.mkdir(parents=True)
    svc.create("e1", "shared", fake_python, comfyui_src)
    svc.create("e2", "shared", fake_python, comfyui_src)
    envs = svc.list_all()
    assert {e.name for e in envs} == {"e1", "e2"}

def test_delete_removes_env(deps):
    svc, _, project_root, _, fake_python = deps
    comfyui_src = project_root.parent / "shared" / "ComfyUI"
    comfyui_src.mkdir(parents=True)
    svc.create("e1", "shared", fake_python, comfyui_src)
    env = svc.list_all()[0]
    assert svc.delete(env.id).ok
    assert svc.list_all() == []

def test_delete_running_requires_force(deps):
    svc, _, project_root, _, fake_python = deps
    comfyui_src = project_root.parent / "shared" / "ComfyUI"
    comfyui_src.mkdir(parents=True)
    svc.create("e1", "shared", fake_python, comfyui_src)
    env = svc.list_all()[0]
    env.status = "running"
    svc.repo.save(env)  # 持久化 running 状态
    result = svc.delete(env.id, force=False)
    assert not result.ok
    assert result.error.code == "ENV_RUNNING"
    assert svc.delete(env.id, force=True).ok

def test_clone_creates_independent_copy(deps):
    svc, _, project_root, _, fake_python = deps
    comfyui_src = project_root.parent / "shared" / "ComfyUI"
    comfyui_src.mkdir(parents=True)
    svc.create("e1", "shared", fake_python, comfyui_src)
    src = svc.list_all()[0]
    result = svc.clone(src.id, "e1-copy")
    assert result.ok
    new_env = result.value
    assert new_env.name == "e1-copy"
    assert new_env.id != src.id
    assert new_env.port != src.port
