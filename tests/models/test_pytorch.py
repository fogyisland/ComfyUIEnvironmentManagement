import pytest
from pathlib import Path
from comfy_mgr.models.pytorch import TorchConfig, DEFAULT_VERSIONS


def test_default_for_cu124():
    cfg = TorchConfig.default_for("cu124", "3.10")
    assert cfg.index_url == "https://download.pytorch.org/whl/cu124"
    assert cfg.cuda_version == "cu124"
    assert cfg.python_version == "3.10"
    assert cfg.torch.startswith("2.")
    assert cfg.torchaudio.startswith("2.")
    assert cfg.torchvision.startswith("0.")
    assert cfg.xformers  # 应该有默认值


def test_default_for_cpu():
    cfg = TorchConfig.default_for("cpu", "3.10")
    assert cfg.index_url == "https://download.pytorch.org/whl/cpu"
    assert cfg.torch  # CPU 版本


def test_yaml_roundtrip(tmp_path):
    cfg = TorchConfig.default_for("cu124", "3.10")
    path = tmp_path / "torch.yaml"
    cfg.save(path)
    loaded = TorchConfig.load(path)
    assert loaded == cfg


def test_install_command_format():
    cfg = TorchConfig.default_for("cu124", "3.10")
    cmd = cfg.install_command()
    assert "torch==" in cmd
    assert "torchaudio==" in cmd
    assert "torchvision==" in cmd
    assert "--index-url" in cmd
    assert "cu124" in cmd
