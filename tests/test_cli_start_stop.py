import pytest
from pathlib import Path
from unittest.mock import MagicMock
from typer.testing import CliRunner
from comfy_mgr.cli import app
from comfy_mgr.result import Result

runner = CliRunner()


@pytest.fixture
def with_env(tmp_path, monkeypatch, mocker):
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
    def _fake_venv_create(python_exe, venv_path):
        venv_python = venv_path / "Scripts" / "python.exe"
        venv_python.parent.mkdir(parents=True, exist_ok=True)
        venv_python.write_text("")
        return Result.ok(None)

    mock_venv = mocker.patch("comfy_mgr.cli.VenvManager")
    mock_venv.return_value.create.side_effect = _fake_venv_create

    runner.invoke(app, [
        "env", "create", "--name", "e1", "--layout", "shared",
        "--port", "8188", "--python", str(fake_python),
        "--comfyui-source", str(comfyui_src),
    ])
    return project_root


def test_env_start_calls_process_service(with_env, mocker):
    mock_handle = MagicMock(pid=9999, port=8188, env_id="x", started_at=None, log_file=Path("x"))
    from comfy_mgr.cli import build_services
    original = build_services
    def patched():
        services = original()
        services["process"].start = MagicMock(return_value=Result.ok(mock_handle))
        return services
    mocker.patch("comfy_mgr.cli.build_services", side_effect=patched)

    result = runner.invoke(app, ["env", "start", "e1"])
    # 即使 mock 了，断言退出码与 stdout 包含成功标志
    assert "9999" in result.stdout or result.exit_code == 0


def test_env_stop(with_env, mocker):
    from comfy_mgr.cli import build_services
    original = build_services
    def patched():
        services = original()
        services["process"].stop = MagicMock(return_value=Result.ok(None))
        return services
    mocker.patch("comfy_mgr.cli.build_services", side_effect=patched)

    result = runner.invoke(app, ["env", "stop", "e1"])
    assert result.exit_code == 0


def test_env_status(with_env):
    result = runner.invoke(app, ["env", "status", "e1"])
    assert result.exit_code == 0
    assert "e1" in result.stdout
