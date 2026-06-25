"""TorchBridge：把 PyTorch 检测/安装暴露给 QML。"""
from __future__ import annotations
from PySide6.QtCore import Property, Signal, Slot
from app.bridge.base import BaseBridge
from comfy_mgr.infra.cuda import CudaDetector
from comfy_mgr.infra.pytorch import PyTorchInstaller
from comfy_mgr.services.environment import EnvironmentService


class TorchBridge(BaseBridge):
    cudaDetected = Signal("QVariant")  # dict
    torchConfigWritten = Signal(str)   # env_id

    def __init__(self, cuda=CudaDetector, pytorch=PyTorchInstaller,
                 environment: EnvironmentService | None = None):
        super().__init__()
        self._cuda = cuda
        self._pytorch = pytorch
        self._environment = environment

    @Slot(result="QVariant")
    def detectCuda(self) -> dict:
        result = self._invoke(self._cuda.detect)
        if result["ok"]:
            self.cudaDetected.emit(result["value"])
        return result

    @Slot(str, str, result="QVariant")
    def initEnvTorch(self, env_id: str, cu_version: str = "") -> dict:
        """M1: 调 EnvironmentService.create 重新走 install_torch 流程。
        M2: 改为原地安装 (不重建 venv)。"""
        if not self._environment:
            return {"ok": False, "error": {
                "code": "TORCH_INIT_FAILED",
                "message": "EnvironmentService 未注入",
            }}
        env = self._environment.get(env_id)
        if not env:
            return {"ok": False, "error": {
                "code": "ENV_NOT_FOUND",
                "message": f"环境 {env_id} 不存在",
            }}
        # M1 简化：用 EnvironmentService.create 重装路径不太合适；
        # 改为直接调 PyTorchInstaller.install
        from comfy_mgr.infra.venv import VenvManager
        from comfy_mgr.models.pytorch import TorchConfig
        ver_result = VenvManager.get_python_version(env.python_executable)
        py_ver = "3.10"
        if ver_result.ok:
            parts = ver_result.value.split()
            if len(parts) >= 2:
                py_ver = parts[1].rsplit(".", 1)[0]
        cu = cu_version or "cu124"
        cfg = TorchConfig.default_for(cu, py_ver)
        cfg.save(env.root_path / ".torch-config.yaml")
        install_result = self._invoke(
            self._pytorch.install, env.python_executable, cfg)
        if install_result["ok"]:
            self.torchConfigWritten.emit(env_id)
        return install_result

    @Property("QVariantList", constant=True)
    def suggestedCuVersions(self) -> list[str]:
        return ["cu118", "cu121", "cu124", "cpu"]