from __future__ import annotations
from dataclasses import dataclass
from pathlib import Path
import yaml


# 默认版本映射（2026-06 时的 PyTorch 稳定版）
DEFAULT_VERSIONS = {
    "cu118": {"torch": "2.4.1+cu118", "torchaudio": "2.4.1+cu118", "torchvision": "0.19.1+cu118", "xformers": "0.0.28+cu118"},
    "cu124": {"torch": "2.5.0+cu124", "torchaudio": "2.5.0+cu124", "torchvision": "0.20.0+cu124", "xformers": "0.0.28.post1+cu124"},
    "cu126": {"torch": "2.6.0+cu126", "torchaudio": "2.6.0+cu126", "torchvision": "0.21.0+cu126", "xformers": "0.0.29+cu126"},
    "cpu":   {"torch": "2.5.0+cpu",   "torchaudio": "2.5.0+cpu",   "torchvision": "0.20.0+cpu",   "xformers": ""},
}


@dataclass
class TorchConfig:
    cuda_version: str  # cu118 / cu124 / cu126 / cpu
    python_version: str  # e.g. "3.10"
    index_url: str
    torch: str
    torchaudio: str
    torchvision: str
    xformers: str  # 空字符串 = 不装

    @classmethod
    def default_for(cls, cu: str, python_version: str) -> "TorchConfig":
        versions = DEFAULT_VERSIONS.get(cu, DEFAULT_VERSIONS["cpu"])
        return cls(
            cuda_version=cu,
            python_version=python_version,
            index_url=f"https://download.pytorch.org/whl/{cu}",
            torch=versions["torch"],
            torchaudio=versions["torchaudio"],
            torchvision=versions["torchvision"],
            xformers=versions["xformers"],
        )

    def install_command(self) -> str:
        pkgs = [
            f"torch=={self.torch}",
            f"torchaudio=={self.torchaudio}",
            f"torchvision=={self.torchvision}",
        ]
        if self.xformers:
            pkgs.append(f"xformers=={self.xformers}")
        return "pip install " + " ".join(pkgs) + f" --index-url {self.index_url}"

    def to_dict(self) -> dict:
        return {
            "cuda_version": self.cuda_version,
            "python_version": self.python_version,
            "index_url": self.index_url,
            "torch": self.torch,
            "torchaudio": self.torchaudio,
            "torchvision": self.torchvision,
            "xformers": self.xformers,
        }

    @classmethod
    def from_dict(cls, d: dict) -> "TorchConfig":
        return cls(**d)

    def save(self, path: Path) -> None:
        with path.open("w", encoding="utf-8") as f:
            yaml.safe_dump(self.to_dict(), f, allow_unicode=True, sort_keys=False)

    @classmethod
    def load(cls, path: Path) -> "TorchConfig":
        with path.open("r", encoding="utf-8") as f:
            return cls.from_dict(yaml.safe_load(f))
