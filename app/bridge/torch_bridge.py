"""TorchBridge：PyTorch 检测/安装（无 PySide6 依赖）。"""
from __future__ import annotations
from comfy_mgr.infra.event_bus import EventBus
from app.bridge.base import BaseBridge
from comfy_mgr.infra.cuda import CudaDetector
from comfy_mgr.infra.pytorch import PyTorchInstaller
from comfy_mgr.services.environment import EnvironmentService


class TorchBridge(BaseBridge):

    def __init__(self, bus: EventBus, cuda=CudaDetector, pytorch=PyTorchInstaller,
                 environment: EnvironmentService | None = None):
        super().__init__(bus)
        self._cuda = cuda
        self._pytorch = pytorch
        self._environment = environment

    def detect_cuda(self) -> dict:
        result = self._invoke(self._cuda.detect)
        if result["ok"]:
            self.bus.emit("ws.push", "cudaDetected", result["value"])
        return result

    def init_env_torch(self, env_id: str, cu_version: str = "") -> dict:
        if not self._environment:
            return {"ok": False, "error": {
                "code": "TORCH_INIT_FAILED", "message": "EnvironmentService 未注入"}}
        env = self._environment.get(env_id)
        if not env:
            return {"ok": False, "error": {
                "code": "ENV_NOT_FOUND", "message": f"环境 {env_id} 不存在"}}
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
        install_result = self._invoke(self._pytorch.install, env.python_executable, cfg)
        if install_result["ok"]:
            self.bus.emit("ws.push", "torchConfigWritten", env_id)
        return install_result

    @property
    def suggested_cu_versions(self) -> list:
        return ["cu118", "cu121", "cu124", "cpu"]
