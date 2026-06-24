from __future__ import annotations
import logging
import subprocess
from pathlib import Path
from comfy_mgr.models.pytorch import TorchConfig
from comfy_mgr.result import Result, ServiceError

log = logging.getLogger(__name__)


class PyTorchInstaller:
    """在 venv 中安装 PyTorch 栈。"""

    @staticmethod
    def install(python_exe: Path, config: TorchConfig) -> Result[None]:
        """先装 torch/torchaudio/torchvision，再尝试 xformers。"""
        # 1. 主包（必须成功）
        main_pkgs = [
            f"torch=={config.torch}",
            f"torchaudio=={config.torchaudio}",
            f"torchvision=={config.torchvision}",
        ]
        main_result = PyTorchInstaller._run_pip(python_exe, main_pkgs, config.index_url)
        if not main_result.ok:
            return main_result
        # 2. xformers（失败仅 warn）
        if config.xformers:
            xformers_pkgs = [f"xformers=={config.xformers}"]
            xformers_result = PyTorchInstaller._run_pip(python_exe, xformers_pkgs, config.index_url)
            if not xformers_result.ok:
                log.warning("xformers 安装失败（不影响 ComfyUI 运行）: %s", xformers_result.error.message)
        return Result.ok(None)

    @staticmethod
    def _run_pip(python_exe: Path, packages: list[str], index_url: str) -> Result[None]:
        cmd = [str(python_exe), "-m", "pip", "install"] + packages + ["--index-url", index_url]
        try:
            result = subprocess.run(cmd, capture_output=True, text=True, timeout=1800)
            if result.returncode != 0:
                return Result.fail(ServiceError(
                    code="PYTORCH_INSTALL_FAILED",
                    message=f"pip install 失败: {result.stderr.strip()[:500]}",
                    detail={"packages": packages, "index_url": index_url},
                ))
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="PYTORCH_INSTALL_FAILED",
                message=str(e),
            ))
