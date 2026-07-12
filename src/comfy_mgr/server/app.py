"""FastAPI app factory + lifespan。"""
from __future__ import annotations
import logging
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI
from app.app_context import AppContext

logger = logging.getLogger(__name__)


def build_app(ctx: AppContext) -> FastAPI:
    """构造 FastAPI app,lifespan 内启动 WS broadcaster + 恢复持久化进程状态,关闭时停所有 env。

    T4 skeleton:WSBroadcaster(T7)和各 route 模块(T8)尚未创建,所以这里用
    懒导入 + try/except ImportError 让 app 仍可构造。当前会得到 routes: 0,
    T7/T8 落地后这里会自动注册更多 router。
    """

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        from app.main import recover_persisted_processes
        recover_persisted_processes(ctx)
        app.state.ctx = ctx
        app.state.bus = ctx.bus
        app.state.environment_bridge = ctx.environment_bridge
        app.state.catalog_bridge = ctx.catalog_bridge
        app.state.node_bridge = ctx.node_bridge
        app.state.bulk_update_service = ctx.bulk_update_service
        app.state.process_bridge = ctx.process_bridge
        app.state.settings_bridge = ctx.settings_bridge
        app.state.torch_bridge = ctx.torch_bridge
        # 启动 process → bridge sink(M3 已有逻辑,改名)
        ctx.process._bridge_sink = ctx.process_bridge._on_line

        # WSBroadcaster(T7 才会创建)— 缺则跳过
        try:
            from comfy_mgr.server.events import WSBroadcaster
            app.state.ws_broadcaster = WSBroadcaster(ctx.bus)
        except ImportError:
            logger.warning("WSBroadcaster 尚未实现(T7),跳过 WS 广播注册")
            app.state.ws_broadcaster = None

        logger.info("server ready")
        yield
        # graceful shutdown
        for env in ctx.environment.list_all():
            if env.status == "running":
                try:
                    ctx.process.stop(env.id, timeout=3)
                except Exception:
                    logger.exception("shutdown: stop %s failed", env.id)

    app = FastAPI(title="ComfyUI Manager", lifespan=lifespan)

    # 注册 routes — health 走根路径,其余按 /api/v1/<name> 命名空间。
    _route_prefixes = {
        "health": "",
        "env": "/api/v1/env",
        "catalog": "/api/v1/catalog",
        "node": "/api/v1/node",
        "process": "/api/v1/process",
        "settings": "/api/v1/settings",
        "torch": "/api/v1/torch",
        "bulk": "/api/v1/bulk-update",
    }
    for name, prefix in _route_prefixes.items():
        try:
            module = __import__(f"comfy_mgr.server.routes.{name}", fromlist=["router"])
            app.include_router(module.router, prefix=prefix)
        except ImportError:
            logger.warning("route %s 尚未实现(T8),跳过", name)

    # WS endpoint
    try:
        from comfy_mgr.server.routes import ws as ws_route
        app.include_router(ws_route.router)
    except ImportError:
        logger.warning("ws route 尚未实现(T8),跳过")

    return app
