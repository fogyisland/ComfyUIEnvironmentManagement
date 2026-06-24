from pathlib import Path
from unittest.mock import MagicMock
from comfy_mgr.infra.pytorch import PyTorchInstaller
from comfy_mgr.models.pytorch import TorchConfig
from comfy_mgr.result import Result


def test_install_runs_pip_install(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.pytorch.subprocess.run")
    mock_run.return_value = MagicMock(returncode=0, stderr="")
    cfg = TorchConfig.default_for("cu124", "3.10")
    python = Path("C:/envs/e1/venv/Scripts/python.exe")
    result = PyTorchInstaller.install(python, cfg)
    assert result.ok
    # Use first call (main packages); second call is xformers only
    args = mock_run.call_args_list[0][0][0]
    assert args[0] == str(python)
    # NOTE: brief had `args[1:3]` but that's only 2 elements; correct is `args[1:4]`
    assert args[1:4] == ["-m", "pip", "install"]
    cmd_str = " ".join(args)
    assert "torch==" in cmd_str
    assert "cu124" in cmd_str


def test_install_skips_xformers_when_empty(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.pytorch.subprocess.run")
    mock_run.return_value = MagicMock(returncode=0, stderr="")
    cfg = TorchConfig.default_for("cpu", "3.10")
    assert cfg.xformers == ""
    result = PyTorchInstaller.install(Path("X"), cfg)
    assert result.ok
    cmd_str = " ".join(mock_run.call_args[0][0])
    assert "xformers" not in cmd_str


def test_install_returns_fail_on_pip_error(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.pytorch.subprocess.run")
    mock_run.return_value = MagicMock(returncode=1, stderr="resolution failed")
    cfg = TorchConfig.default_for("cu124", "3.10")
    result = PyTorchInstaller.install(Path("X"), cfg)
    assert not result.ok
    assert result.error.code == "PYTORCH_INSTALL_FAILED"


def test_install_returns_fail_on_xformers_only(mocker):
    """xformers 单独失败不应阻断其他三个。"""
    # 第一次调用（torch/torchaudio/torchvision）成功
    # 第二次调用（xformers）失败 - 但 PyTorchInstaller 应对此 warn
    mock_run = mocker.patch("comfy_mgr.infra.pytorch.subprocess.run")
    mock_run.side_effect = [
        MagicMock(returncode=0, stderr=""),
        MagicMock(returncode=1, stderr="no matching xformers"),
    ]
    cfg = TorchConfig.default_for("cu124", "3.10")
    result = PyTorchInstaller.install(Path("X"), cfg)
    assert result.ok  # 整体成功，xformers 失败仅 warn
    assert mock_run.call_count == 2
