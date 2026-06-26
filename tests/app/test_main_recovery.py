"""app.main.recover_persisted_processes 回归测试。"""
from __future__ import annotations
from datetime import datetime
from pathlib import Path
from unittest.mock import patch

from comfy_mgr.models.environment import Environment
from comfy_mgr.models.process_state import ProcessState


def _env_obj(env_id="env-dead"):
    return Environment(
        id=env_id, name=env_id,
        root_path=Path("D:/envs/x"), comfyui_layout="shared",
        comfyui_source=Path("C:/ComfyUI"),
        venv_path=Path("D:/envs/x/v"),
        python_executable=Path("D:/envs/x/v/Scripts/python.exe"),
        custom_nodes_path=Path("D:/envs/x/cn"),
        extra_model_paths_yaml=Path("D:/envs/x/yaml"),
        port=8188,
        status="stopped", pid=None,
    )


def test_recover_skips_dead_pid(qapp, tmp_path, monkeypatch):
    """死掉的 PID 不能被标为 running — 之前的 bug：所有持久 state 一律 running。"""
    fake_appdata = tmp_path / "appdata"
    monkeypatch.setenv("APPDATA", str(fake_appdata))

    from app.app_context import AppContext
    ctx = AppContext(project_root=tmp_path)

    # 注入一个 env 和一个 state（PID = 99999 一定死）
    env = _env_obj("env-dead")
    ctx.environment.repo.save(env)

    dead_pid = 99999  # 极不可能存在的 PID
    state = ProcessState(
        env_id="env-dead", pid=dead_pid, port=8188,
        started_at=datetime.now(),
    )
    ctx.process._state_repo.save(state)

    # 在 Windows/POSIX 上 os.kill(99999, 0) 都会抛异常
    from app.main import recover_persisted_processes
    recover_persisted_processes(ctx)

    # env 应该被改回 stopped
    refreshed = ctx.environment.get("env-dead")
    assert refreshed.status == "stopped"
    assert refreshed.pid is None
    # state 应该被清掉
    assert ctx.process._state_repo.get("env-dead") is None


def test_recover_keeps_alive_pid(qapp, tmp_path, monkeypatch):
    """活着的 PID 应该被标 running。"""
    fake_appdata = tmp_path / "appdata"
    monkeypatch.setenv("APPDATA", str(fake_appdata))

    from app.app_context import AppContext
    ctx = AppContext(project_root=tmp_path)

    env = _env_obj("env-alive")
    ctx.environment.repo.save(env)

    # 用当前进程的 PID — 它一定活着
    import os
    alive_pid = os.getpid()
    state = ProcessState(
        env_id="env-alive", pid=alive_pid, port=8188,
        started_at=datetime.now(),
    )
    ctx.process._state_repo.save(state)

    from app.main import recover_persisted_processes
    recover_persisted_processes(ctx)

    refreshed = ctx.environment.get("env-alive")
    assert refreshed.status == "running"
    assert refreshed.pid == alive_pid
    # state 应保留
    assert ctx.process._state_repo.get("env-alive") is not None


def test_recover_cleans_orphan_state(qapp, tmp_path, monkeypatch):
    """state 存在但 env 已被删 — 直接清 state。"""
    fake_appdata = tmp_path / "appdata"
    monkeypatch.setenv("APPDATA", str(fake_appdata))

    from app.app_context import AppContext
    ctx = AppContext(project_root=tmp_path)

    # 只插入 state 不插入 env
    state = ProcessState(
        env_id="env-orphan", pid=12345, port=8188,
        started_at=datetime.now(),
    )
    ctx.process._state_repo.save(state)

    from app.main import recover_persisted_processes
    recover_persisted_processes(ctx)

    assert ctx.process._state_repo.get("env-orphan") is None