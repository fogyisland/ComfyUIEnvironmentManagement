import pytest
from pathlib import Path
from typer.testing import CliRunner
from comfy_mgr.cli import app
from comfy_mgr.infra.cuda import CudaInfo
from comfy_mgr.result import Result

runner = CliRunner()


@pytest.fixture
def isolated(tmp_path, monkeypatch):
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    project_root = tmp_path / "project"
    project_root.mkdir()
    comfyui_src = tmp_path / "shared" / "ComfyUI"
    comfyui_src.mkdir(parents=True)
    monkeypatch.chdir(project_root)
    return project_root


def test_torch_detect_with_gpu(isolated, mocker):
    mocker.patch("comfy_mgr.cli.CudaDetector.detect", return_value=Result.ok(
        CudaInfo("596.36", "13.2", "NVIDIA GeForce RTX 4060", True)
    ))
    result = runner.invoke(app, ["torch", "detect"])
    assert result.exit_code == 0
    assert "RTX 4060" in result.output
    assert "13.2" in result.output
    assert "cu124" in result.output


def test_torch_detect_without_gpu(isolated, mocker):
    mocker.patch("comfy_mgr.cli.CudaDetector.detect", return_value=Result.ok(
        CudaInfo(None, None, None, False)
    ))
    result = runner.invoke(app, ["torch", "detect"])
    assert result.exit_code == 0
    assert "未检测到" in result.output
    assert "cpu" in result.output


def test_torch_init_writes_config(isolated, mocker):
    # Create fake_python and mock VenvManager so env create succeeds without real venv.
    fake_python = isolated.parent / "fake_python" / "python.exe"
    fake_python.parent.mkdir(parents=True)
    fake_python.write_text("")

    def _fake_venv_create(python_exe, venv_path):
        venv_python = venv_path / "Scripts" / "python.exe"
        venv_python.parent.mkdir(parents=True, exist_ok=True)
        venv_python.write_text("")
        return Result.ok(None)

    mock_venv = mocker.patch("comfy_mgr.cli.VenvManager")
    mock_venv.return_value.create.side_effect = _fake_venv_create

    comfyui_src = isolated.parent / "shared" / "ComfyUI"
    comfyui_src.mkdir(parents=True, exist_ok=True)

    runner.invoke(app, [
        "env", "create", "--name", "e1", "--layout", "shared",
        "--port", "8188", "--python", str(fake_python),
        "--comfyui-source", str(comfyui_src),
    ])

    mocker.patch("comfy_mgr.cli.CudaDetector.detect", return_value=Result.ok(
        CudaInfo("596.36", "13.2", "RTX 4060", True)
    ))
    mocker.patch("comfy_mgr.cli.VenvManager.get_python_version",
                 return_value=Result.ok("Python 3.10.5"))
    result = runner.invoke(app, ["torch", "init", "--env", "e1", "--cu", "cu124", "--non-interactive"])
    assert result.exit_code == 0, result.output
    cfg_path = isolated / "envs" / "e1" / ".torch-config.yaml"
    assert cfg_path.exists()
    import yaml
    data = yaml.safe_load(cfg_path.read_text())
    assert data["cuda_version"] == "cu124"
    assert data["python_version"] == "3.10"