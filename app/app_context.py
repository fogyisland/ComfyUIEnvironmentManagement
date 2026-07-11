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
from comfy_mgr.services.bulk_update_service import BulkUpdateService
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
        # EventBus: must be created before any bridge construction, since every
        # bridge requires `bus: EventBus` as a kwarg (T1 refactor removed PySide6).
        # Later tasks (T10 server lifespan) can subscribe without re-allocating.
        self.bus = EventBus()
        # Bridges
        self.environment_bridge = EnvironmentBridge(self.environment, bus=self.bus)
        self.catalog_bridge = CatalogBridge(self.catalog, bus=self.bus)
        self.process_bridge = ProcessBridge(self.process, bus=self.bus)
        self.settings_bridge = SettingsBridge(self.settings, bus=self.bus)
        self.torch_bridge = TorchBridge(
            environment=self.environment, pytorch=self.pytorch,
            bus=self.bus,
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
        # 注意:self.bus 已在 bridges 段之前创建(T1 refactor 后 bridges 必须
        # 接受 bus kwarg),这里不再重复赋值。
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
        # scanned 是 per-env 实例(env_id 在切换时才知道),这里先传 None 占位。
        # QML 端在 EnvironmentDetailPanel.Component.onCompleted 调
        # `appContext.node_bridge.setScannedService(
        #      appContext.scanned_node_service(currentEnvId))`
        # 完成 per-env wiring(见 node_bridge.setScannedService Slot)。
        # 注意:此处先用 None 占位,M3 服务创建完毕后会在下方统一注入。
        self.node_bridge = NodeBridge(
            m0_service=self.node,
            scanned_node_service=None,    # lazy:QML 切换 env 时 setScannedService
            conflict_service=self.conflict_service,
            node_meta_service=self.node_meta_service,
            bus=self.bus,
        )

        # ============ M5 新增 ============
        # BulkUpdateService:跨 env × 节点批量 update(M5 T1)
        self.bulk_update_service = BulkUpdateService(
            node_bridge=self.node_bridge, bus=self.bus,
        )

        # ============ M3 新增 ============
        # M3 review Critical 教训(沿用 M2):新服务必须显式 wiring + 回归测试。
        # 这里只 APPEND,不动 M0/M1/M2 已有属性。
        from comfy_mgr.db.scanned_node_repo import ScannedNodeRepo
        from comfy_mgr.infra.http_client import HTTPClient
        from comfy_mgr.infra.catalog_http_client import CatalogHTTPClient
        from comfy_mgr.infra.compat_http_client import CompatHTTPClient
        from comfy_mgr.infra.git_portable import default_git_resolver
        from comfy_mgr.infra.python_portable import default_python_resolver
        from comfy_mgr.db.version_repo import VersionRepo
        from comfy_mgr.db.dep_repo import DepRepo
        from comfy_mgr.db.catalog_repo import CatalogCacheRepo
        from comfy_mgr.services.version import VersionService
        from comfy_mgr.services.dependency import DepService
        from comfy_mgr.services.install import InstallService

        # SettingsService.get 不支持默认值,这里手动 fallback(沿用 M2 node_meta 模式)。
        _http_timeout = self.settings.get("http_timeout")
        if _http_timeout is None:
            _http_timeout = 10.0
        _http_max_retries = self.settings.get("http_max_retries")
        if _http_max_retries is None:
            _http_max_retries = 3
        _catalog_api_base_url = self.settings.get("catalog_api_base_url")
        if _catalog_api_base_url is None:
            _catalog_api_base_url = "https://api.comfy.org"
        ttl_min = self.settings.get("catalog_cache_ttl_minutes") or 60
        _catalog_cache_ttl = int(ttl_min) * 60
        _compat_api_base_url = self.settings.get("compat_api_base_url") or ""

        self.http_client = HTTPClient(
            timeout=_http_timeout,
            max_retries=_http_max_retries,
        )
        self.catalog_client = CatalogHTTPClient(
            catalog_repo=CatalogCacheRepo(self.conn),
            http_client=self.http_client,
            base_url=_catalog_api_base_url,
            cache_ttl_seconds=_catalog_cache_ttl,
        )
        self.compat_client = self._build_compat_client()
        # git resolver closure over project_root
        self._git_exe_resolver = lambda: default_git_resolver(self.project_root)
        # python resolver closure over project_root (M3 新加)
        self._python_exe_resolver = (
            lambda: default_python_resolver(self.project_root)
        )

        self.version_service = VersionService(
            version_repo=VersionRepo(self.conn),
            scanned_repo=ScannedNodeRepo(self.conn),
            conn=self.conn, event_bus=self.bus,
            git_exe_resolver=self._git_exe_resolver,
        )
        self.dep_service = DepService(
            dep_repo=DepRepo(self.conn),
            scanned_repo=ScannedNodeRepo(self.conn),
            conn=self.conn, bus=self.bus,
            compat_client=self.compat_client,
        )
        self.install_service = InstallService(
            scanned_repo=ScannedNodeRepo(self.conn),
            version_repo=VersionRepo(self.conn),
            conn=self.conn, bus=self.bus,
            git_exe_resolver=self._git_exe_resolver,
        )

        # M3 注入到 NodeBridge(NodeBridge 在 M2 段已建,这里补 M3 deps)
        self.node_bridge.version = self.version_service
        self.node_bridge.dep = self.dep_service
        self.node_bridge.catalog = self.catalog_client
        self.node_bridge.compat = self.compat_client
        self.node_bridge.install = self.install_service
        self.node_bridge._project_root = self.project_root
        self.node_bridge._git_exe_resolver = (
            lambda: default_git_resolver(self.project_root)
        )

        # 一次性 mkdir 迁移:M1 老 env 可能没有 custom_nodes/ 目录,这里补建。
        self._migrate_create_custom_nodes_dirs()

        # M3+:节点目录自动刷新。如果用户开启(catalog_auto_refresh=True),
        # 启动时启动一个 QTimer 后台定时拉全量 catalog 写本地 cache。
        self._catalog_auto_refresh_timer = None
        if self.settings.get("catalog_auto_refresh") is not False:
            self._start_catalog_auto_refresh()

    def _build_compat_client(self) -> CompatHTTPClient:
        base_url = self.settings.get("compat_api_base_url") or ""
        return CompatHTTPClient(
            base_url=base_url,
            http_client=self.http_client,
        )

    def _start_catalog_auto_refresh(self) -> None:
        """启动 catalog 后台自动刷新:首次延迟 5s 触发(让 UI 先起来),
        之后按 catalog_auto_refresh_minutes 间隔循环。"""
        from PySide6.QtCore import QTimer
        minutes = self.settings.get("catalog_auto_refresh_minutes") or 360
        # 启动后立即在后台拉一次(force_refresh=True,绕过 TTL 缓存)
        QTimer.singleShot(5_000, self._refresh_catalog_now)
        # 之后按 minutes 间隔循环
        timer = QTimer()
        timer.setInterval(max(1, minutes) * 60 * 1000)
        timer.timeout.connect(self._refresh_catalog_now)
        timer.start()
        self._catalog_auto_refresh_timer = timer

    def _refresh_catalog_now(self) -> None:
        """后台拉一次全量 catalog,失败不报错(走 stale 降级)。"""
        try:
            r = self.catalog_client.list_remote(force_refresh=True)
            if r.ok:
                self.bus.emit("catalogUpdated", len(r.value))
        except Exception:
            # 后台任务,任何异常都吞掉,不污染主流程
            pass

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
