"""AppContext：依赖注入容器（service container + Bridge factory）。"""
from __future__ import annotations
from pathlib import Path
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.infra.fs import FS
from comfy_mgr.infra.git import GitManager
from comfy_mgr.infra.venv import VenvManager
from comfy_mgr.infra.process import ProcessService
from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.infra.node_scanner import NodeScanner
from comfy_mgr.infra.github_client import GitHubClient
from comfy_mgr.models.process_state import ProcessStateRepo
from comfy_mgr.infra.pytorch import PyTorchInstaller
from comfy_mgr.settings import SettingsService
from comfy_mgr.services.environment import EnvironmentService
from comfy_mgr.services.catalog import CatalogService
from comfy_mgr.services.node import NodeService
from comfy_mgr.services.scanned_node import ScannedNodeService
from comfy_mgr.services.conflict import ConflictService
from comfy_mgr.services.node_meta import NodeMetaService
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
        # ProcessService → ProcessBridge wiring
        # Build ProcessService first (without a sink — bridge doesn't exist yet),
        # then create ProcessBridge(self.process) which captures a reference,
        # then rebind the sink to the bridge's _on_line. The old code set the
        # wrong attribute name `bridge_sink` (the constructor stores it under
        # `_bridge_sink`), so live logs never reached QML. We can't pass
        # `bridge_sink=self.process_bridge._on_line` to ProcessService here
        # because ProcessBridge requires `self.process` already to exist.
        self.process = ProcessService(
            conn=self.conn,
            log_dir=self.project_root / "logs",
            process_state_repo=ProcessStateRepo(self.conn),
        )
        # Bridges
        self.environment_bridge = EnvironmentBridge(self.environment)
        self.catalog_bridge = CatalogBridge(self.catalog)
        self.process_bridge = ProcessBridge(self.process)
        self.settings_bridge = SettingsBridge(self.settings)
        self.torch_bridge = TorchBridge(
            environment=self.environment, pytorch=self.pytorch,
        )
        # Wire process → process_bridge (live log line forwarder).
        # Note: attribute is `_bridge_sink` (private on ProcessService), not
        # `bridge_sink` — the old bug wrote to the latter and dropped all logs.
        self.process._bridge_sink = self.process_bridge._on_line
        # ProcessBridge needs to resolve env_id → Environment
        self.process_bridge.set_env_resolver(
            lambda eid: self.environment.get(eid)
        )

        # ============ M2 新增 ============
        # M1 review Critical #1 教训:新服务必须显式 wiring,且有回归测试覆盖。
        # 这里只 APPEND,不动 M0/M1 已有属性。
        self.bus = EventBus()
        self.scanner = NodeScanner()
        self.github_client = GitHubClient()

        # per-env ScannedNodeService factory(closure over conn/scanner/bus)。
        # 不存 instance,需要时由调用方现场建(env_id 在调用时才知道)。
        def make_scanned_node_service(env_id: str) -> ScannedNodeService:
            return ScannedNodeService(
                conn=self.conn, env_id=env_id,
                scanner=self.scanner, bus=self.bus,
            )
        self.scanned_node_service = make_scanned_node_service

        # ConflictService:node_service 懒绑定,detect() 直接走 SQL 不需要。
        self.conflict_service = ConflictService(
            conn=self.conn, bus=self.bus, node_service=None,
        )

        # NodeMetaService:SettingsService.get 不支持默认值,这里手动 fallback。
        cache_ttl = self.settings.get("meta_cache_ttl")
        if cache_ttl is None:
            cache_ttl = 3600
        self.node_meta_service = NodeMetaService(
            conn=self.conn, github=self.github_client,
            cache_ttl_seconds=cache_ttl,
        )

        # NodeBridge (M2):m0_service + per-env scanned + conflict + meta + bus。
        # scanned_node_service 接受 per-env instance,这里先传 None 占位,AppContext
        # 暴露 scanned_node_service factory 给 QML 端在切换 env 时调
        # `node_bridge.scanned = ctx.scanned_node_service(env_id)`。
        self.node_bridge = NodeBridge(
            m0_service=self.node,
            scanned_node_service=None,    # lazy:切换 env 时 set
            conflict_service=self.conflict_service,
            node_meta_service=self.node_meta_service,
            bus=self.bus,
        )

        # 一次性 mkdir 迁移:M1 老 env 可能没有 custom_nodes/ 目录,这里补建。
        self._migrate_create_custom_nodes_dirs()

    def _migrate_create_custom_nodes_dirs(self) -> None:
        """M1 没建 custom_nodes/,M2 启动时补建空目录。

        容错:list_all 失败或单个 mkdir 失败都不阻塞启动,只 log。
        """
        import logging
        logger = logging.getLogger(__name__)
        try:
            envs = self.environment.list_all()
        except Exception as e:
            logger.warning("migration: list envs failed: %s", e)
            return
        for env in envs:
            try:
                env.custom_nodes_path.mkdir(parents=True, exist_ok=True)
            except Exception as e:
                logger.warning(
                    "migration: mkdir %s failed for env %s: %s",
                    env.custom_nodes_path, env.id, e,
                )
