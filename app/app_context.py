"""AppContext：依赖注入容器（service container + Bridge factory）。"""
from __future__ import annotations
from pathlib import Path
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.infra.fs import FS
from comfy_mgr.infra.git import GitManager
from comfy_mgr.infra.venv import VenvManager
from comfy_mgr.infra.process import ProcessService
from comfy_mgr.models.process_state import ProcessStateRepo
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
        # T8 签名：ProcessService(conn, log_dir, bridge_sink, process_state_repo)
        self.process = ProcessService(
            conn=self.conn,
            log_dir=self.project_root / "logs",
            process_state_repo=ProcessStateRepo(self.conn),
        )
        self.pytorch = PyTorchInstaller
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
        self.torch_bridge = TorchBridge(
            environment=self.environment, pytorch=self.pytorch,
        )
        # Wire process → process_bridge (Signal forwarder)
        self.process.bridge_sink = self.process_bridge._on_line  # type: ignore[attr-defined]
        # ProcessBridge needs to resolve env_id → Environment
        self.process_bridge.set_env_resolver(
            lambda eid: self.environment.get(eid)
        )