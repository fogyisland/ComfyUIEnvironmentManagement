import pytest
from pathlib import Path
from unittest.mock import MagicMock, patch
from typer.testing import CliRunner
from comfy_mgr.cli import app
from comfy_mgr.result import Result

runner = CliRunner()


@pytest.fixture
def env_setup(tmp_path, monkeypatch, mocker):
    """准备一个临时项目根 + mock 出 SettingsService 让 db 走 tmp_path。"""
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    project_root = tmp_path / "project"
    project_root.mkdir()
    comfyui_src = tmp_path / "shared" / "ComfyUI"
    comfyui_src.mkdir(parents=True)
    (comfyui_src / "main.py").write_text("# comfy")
    fake_python = tmp_path / "fake_python" / "python.exe"
    fake_python.parent.mkdir(parents=True)
    fake_python.write_text("")
    monkeypatch.chdir(project_root)

    # Mock VenvManager to avoid actual venv creation.
    # Use side_effect to physically create the venv python.exe so subsequent
    # clone() calls (which read src.python_executable) find the file.
    def _fake_venv_create(python_exe, venv_path):
        venv_python = venv_path / "Scripts" / "python.exe"
        venv_python.parent.mkdir(parents=True, exist_ok=True)
        venv_python.write_text("")
        return Result.ok(None)

    mock_venv = mocker.patch("comfy_mgr.cli.VenvManager")
    mock_venv.return_value.create.side_effect = _fake_venv_create

    return project_root, comfyui_src, fake_python


def test_env_create_then_list(env_setup):
    project_root, comfyui_src, fake_python = env_setup
    result = runner.invoke(app, [
        "env", "create",
        "--name", "e1",
        "--layout", "shared",
        "--port", "8188",
        "--python", str(fake_python),
        "--comfyui-source", str(comfyui_src),
    ])
    assert result.exit_code == 0, result.stdout
    assert "创建成功" in result.stdout or "e1" in result.stdout

    result = runner.invoke(app, ["env", "list"])
    assert result.exit_code == 0
    assert "e1" in result.stdout


def test_env_create_duplicate_fails(env_setup):
    project_root, comfyui_src, fake_python = env_setup
    args = [
        "env", "create", "--name", "e1", "--layout", "shared",
        "--port", "8188", "--python", str(fake_python),
        "--comfyui-source", str(comfyui_src),
    ]
    runner.invoke(app, args)
    result = runner.invoke(app, args)
    assert result.exit_code != 0
    assert "已存在" in result.output or "duplicate" in result.output.lower()


def test_env_delete(env_setup):
    project_root, comfyui_src, fake_python = env_setup
    runner.invoke(app, [
        "env", "create", "--name", "e1", "--layout", "shared",
        "--port", "8188", "--python", str(fake_python),
        "--comfyui-source", str(comfyui_src),
    ])
    result = runner.invoke(app, ["env", "delete", "e1", "--force"])
    assert result.exit_code == 0

    result = runner.invoke(app, ["env", "list"])
    assert "e1" not in result.stdout


def test_env_clone(env_setup):
    project_root, comfyui_src, fake_python = env_setup
    runner.invoke(app, [
        "env", "create", "--name", "e1", "--layout", "shared",
        "--port", "8188", "--python", str(fake_python),
        "--comfyui-source", str(comfyui_src),
    ])
    result = runner.invoke(app, ["env", "clone", "e1", "e1-copy"])
    assert result.exit_code == 0
    result = runner.invoke(app, ["env", "list"])
    assert "e1-copy" in result.stdout
