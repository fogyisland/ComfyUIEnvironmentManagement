"""AppContext：依赖注入容器（service container + Bridge factory）。

T4 已知偏差（待后续 task 修复）：
- ``comfy_mgr.services.torch_helper.TorchHelper`` 尚未实现（M0 没有此模块）。
  本文件提供一个本地轻量替代品，仅保存 cuda / pytorch 句柄以便未来扩展。
  T14 将引入真正的 ``TorchHelper``，届时替换。
- ``ProcessService`` 的 M0 构造签名是 ``(log_dir)``；T8 会改写为
  ``(conn, log_dir, bridge_sink)``。T4 暂以 M0 形式构造，然后动态
  给实例添加 ``bridge_sink`` 属性供 ``process_bridge._on_line`` 接收。
"""
from __future__ import annotations
from pathlib import Path
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.infra.fs import FS
from comfy_mgr.infra.git import GitManager
from comfy_mgr.infra.venv import VenvManager
from comfy_mgr.infra.process import ProcessService
from comfy_mgr.infra.cuda import CudaDetector
from comfy_mgr.infra.pytorch import PyTorchInstaller
from comfy_mgr.settings import SettingsService
from comfy_mgr.services.environment import EnvironmentService
from comfy_mgr.services.catalog import CatalogService
from comfy_mgr.services.node import NodeService
from app.bridge.environment_bridge import EnvironmentBridge
from app.bridge.catalog_bridge import CatalogBridge
from app.bridge.node_bridge import NodeBridge
from app.bridge.process_bridge import ProcessBridge
from app.bridge.settings_bridge import SettingsBridge
from app.bridge.torch_bridge import TorchBridge


class TorchHelper:
    """T4 桩：组合 CudaDetector + PyTorchInstaller 占位。

    T14 引入真正的 ``comfy_mgr.services.torch_helper.TorchHelper`` 后，
    AppContext 将直接 import 之并删除本类。
    """

    def __init__(self, cuda: type, pytorch: type) -> None:
        self.cuda = cuda
        self.pytorch = pytorch


class AppContext:
    """聚合所有 Service 和 Bridge 实例，供 main.py 注入到 QML。"""

    def __init__(self, project_root: Path | None = None) -> None:
        self.settings = SettingsService()
        db_path = self.settings.resolve_db_path()
        db_path.parent.mkdir(parents=True, exist_ok=True)
        self.conn = get_connection(db_path)
        init_schema(self.conn)
        self.project_root = project_root or Path.cwd()
        self.fs = FS()
        self.git = GitManager()
        self.venv = VenvManager()
        # M0 签名：ProcessService(log_dir)。T8 会重写为接受 conn / bridge_sink。
        self.process = ProcessService(
            log_dir=self.project_root / "logs",
        )
        # T4 临时给 M0 ProcessService 添加 bridge_sink 槽，T8 后将由构造函数接管。
        self.process.bridge_sink = None  # type: ignore[attr-defined]
        self.cuda = CudaDetector
        self.pytorch = PyTorchInstaller
        self.torch_helper = TorchHelper(cuda=self.cuda, pytorch=self.pytorch)
        self.environment = EnvironmentService(
            conn=self.conn, project_root=self.project_root,
            fs=self.fs, venv=self.venv, pytorch=self.pytorch,
        )
        self.catalog = CatalogService(
            conn=self.conn, git=self.git,
            catalog_root=self.project_root / "catalog" / "nodes",
        )
        self.node = NodeService(
            conn=self.conn, fs=self.fs, env_repo=self.environment.repo,
        )
        # Bridges
        self.environment_bridge = EnvironmentBridge(self.environment)
        self.catalog_bridge = CatalogBridge(self.catalog)
        self.node_bridge = NodeBridge(self.node)
        self.process_bridge = ProcessBridge(self.process)
        self.settings_bridge = SettingsBridge(self.settings)
        self.torch_bridge = TorchBridge(self.torch_helper)
        # Wire process → process_bridge (Signal forwarder)
        self.process.bridge_sink = self.process_bridge._on_line  # type: ignore[attr-defined]
        # ProcessBridge needs to resolve env_id → Environment
        self.process_bridge.set_env_resolver(
            lambda eid: self.environment.get(eid)
        )
