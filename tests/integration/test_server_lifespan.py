"""Lifespan 集成测试。"""
from __future__ import annotations
import pytest
from fastapi.testclient import TestClient


def test_healthz_blocked_before_lifespan_start(app_only):
    """lifespan 没启动前,/healthz 仍可访问(非 lifespan 路由)。

    注意:实际上 TestClient(app) 不带 with 时也可以发请求,
    lifespan 只在 with TestClient 上下文时启动。
    这里用 with TestClient 启动 lifespan,然后验证 /healthz 仍 OK。
    """
    with TestClient(app_only) as client:
        r = client.get("/healthz")
        assert r.status_code == 200


def test_ws_broadcaster_attached_during_lifespan(app_only):
    with TestClient(app_only) as client:
        assert hasattr(app_only.state, "ws_broadcaster")
        assert app_only.state.ws_broadcaster is not None


def test_recover_persisted_processes_runs_on_startup(isolated_appdata, sqlite_cross_thread):
    """模拟残留 PID 不存在 → lifespan 启动后 env 状态应为 stopped。

    recover_persisted_processes 在 env 已删时直接清掉 state(env 为 None 时 delete),
    或者 PID 不存在时把 env 改回 stopped 并清 state。
    """
    from datetime import datetime
    from comfy_mgr.app_context import AppContext
    from comfy_mgr.models.process_state import ProcessStateRepo
    from comfy_mgr.models.environment import EnvironmentRepo, Environment
    from comfy_mgr.server.app import build_app

    db_path = isolated_appdata / "catalog.db"
    # 先插入 env + 一条 process_state 指向不存在的 PID
    ctx_init = AppContext()
    env_repo = EnvironmentRepo(ctx_init.conn)
    env = Environment(
        id="env-x", name="env-x",
        root_path=isolated_appdata / "env-x",
        comfyui_layout="isolated", comfyui_source=None,
        venv_path=isolated_appdata / "venv",
        python_executable=isolated_appdata / "venv" / "python",
        custom_nodes_path=isolated_appdata / "cn",
        extra_model_paths_yaml=isolated_appdata / "emp.yaml",
        port=8188,
    )
    env_repo.save(env)
    ProcessStateRepo(ctx_init.conn).save(
        # type: ignore[arg-type]
        type("S", (), {
            "env_id": "env-x", "pid": 999999, "port": 8188,
            "started_at": datetime.now(),
        })()
    )

    # 现在构造 server app,启 lifespan 应触发 recover_persisted_processes
    ctx = AppContext()
    app = build_app(ctx)
    with TestClient(app):
        env_after = ctx.environment.get("env-x")
        # PID 999999 不存在 → env.status 应被改回 stopped
        assert env_after is not None
        assert env_after.status == "stopped"


def test_lifespan_shutdown_stops_running_envs(isolated_appdata, sqlite_cross_thread):
    """lifespan 关闭时,所有 running env 应被 stop 调用。"""
    from comfy_mgr.app_context import AppContext
    from comfy_mgr.server.app import build_app
    from comfy_mgr.models.environment import Environment

    ctx = AppContext()
    app = build_app(ctx)

    stop_calls: list[str] = []

    # mock process.stop 记录被停止的 env_id
    real_process = ctx.process

    def fake_stop(env_id, timeout=3):
        stop_calls.append(env_id)
        return {"ok": True, "value": None}

    real_process.stop = fake_stop

    # 注入一个 status=running 的 env 到 environment repo
    from comfy_mgr.models.environment import EnvironmentRepo
    env = Environment(
        id="env-x", name="env-x",
        root_path=isolated_appdata / "env-x",
        comfyui_layout="isolated", comfyui_source=None,
        venv_path=isolated_appdata / "venv",
        python_executable=isolated_appdata / "venv" / "python",
        custom_nodes_path=isolated_appdata / "cn",
        extra_model_paths_yaml=isolated_appdata / "emp.yaml",
        port=8188, status="running",
    )
    EnvironmentRepo(ctx.conn).save(env)

    with TestClient(app):
        pass  # 退出 with → lifespan shutdown

    assert "env-x" in stop_calls