"""FastAPI app factory + lifespan。"""
from __future__ import annotations
import logging
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI
from comfy_mgr.app_context import AppContext

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

    # 注册 routes(T8 才会创建)— 缺则跳过,保留骨架可构造
    _route_modules = ("health", "env", "catalog", "node", "process", "settings", "torch")
    for name in _route_modules:
        try:
            module = __import__(f"comfy_mgr.server.routes.{name}", fromlist=["router"])
            app.include_router(module.router)
        except ImportError:
            logger.warning("route %s 尚未实现(T8),跳过", name)

    # WS endpoint
    try:
        from comfy_mgr.server.routes import ws as ws_route
        app.include_router(ws_route.router)
    except ImportError:
        logger.warning("ws route 尚未实现(T8),跳过")

    # 显式 prefix 给那些已有 router 的 module(只为后面 T8 落地时行为正确保留)
    # 暂不强制 — T8 来时再确认 prefix 是否需要在 include 时指定。

    return app