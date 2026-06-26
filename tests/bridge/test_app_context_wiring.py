"""AppContext M2 wiring 回归测试(防 M1 Critical #1 重演)。"""
from __future__ import annotations
from pathlib import Path
import pytest
from app.app_context import AppContext


def test_appcontext_has_event_bus(tmp_path: Path, monkeypatch, qapp):
    """AppContext 启动后 self.bus 必须存在且是 EventBus 实例。"""
    # 用临时 APPDATA 隔离 settings / db,避免污染用户配置
    fake_appdata = tmp_path / "appdata"
    monkeypatch.setenv("APPDATA", str(fake_appdata))
    ctx = AppContext(project_root=tmp_path)
    assert ctx.bus is not None
    from comfy_mgr.infra.event_bus import EventBus
    assert isinstance(ctx.bus, EventBus)


def test_appcontext_has_scanned_node_services(tmp_path: Path, monkeypatch, qapp):
    """AppContext 必须暴露 scanned_node_service factory + 两个全局服务。"""
    fake_appdata = tmp_path / "appdata"
    monkeypatch.setenv("APPDATA", str(fake_appdata))
    ctx = AppContext(project_root=tmp_path)
    assert ctx.scanned_node_service is not None
    assert ctx.conflict_service is not None
    assert ctx.node_meta_service is not None
    # factory 可调用,返回 ScannedNodeService 实例
    from comfy_mgr.services.scanned_node import ScannedNodeService
    # factory 需要一个 env_id 才能构造;用一个 fake env_id 即可(不调用 DB)
    # 仅验证可调用性 — 这里只检查 callable,具体实例化留到 T12
    assert callable(ctx.scanned_node_service)


def test_appcontext_creates_custom_nodes_dirs(
    tmp_path: Path, monkeypatch, qapp,
):
    """M1 env 没有 custom_nodes/,M2 启动时应当 mkdir。"""
    fake_appdata = tmp_path / "appdata"
    monkeypatch.setenv("APPDATA", str(fake_appdata))
    ctx = AppContext(project_root=tmp_path)

    # 直接通过 EnvironmentRepo 写一个 env(模拟 M1 老 env 状态,
    # custom_nodes_path 存在但目录本身不存在)
    cn_dir = tmp_path / "envs" / "e1" / "custom_nodes"
    assert not cn_dir.exists()
    env = _make_env(id="env-e1", name="e1", custom_nodes_path=cn_dir)
    assert ctx.environment.repo.save(env).ok
    # 再次确认目录没被 create() 副作用建出来(repo.save 不会触发)
    assert not cn_dir.exists()

    # 重新构造一个 AppContext(模拟"下次启动")→ 触发迁移 mkdir
    ctx2 = AppContext(project_root=tmp_path)
    loaded = ctx2.environment.get("env-e1")
    assert loaded is not None
    assert loaded.custom_nodes_path.exists(), (
        f"custom_nodes_path should be created by migration: "
        f"{loaded.custom_nodes_path}"
    )
    assert loaded.custom_nodes_path.is_dir()


def _make_env(**overrides):
    """构造测试用 Environment,默认字段尽量真实。"""
    from comfy_mgr.models.environment import Environment
    defaults = dict(
        id="env-test",
        name="env1",
        root_path=Path("D:/envs/env1"),
        comfyui_layout="shared",
        comfyui_source=Path("D:/shared/ComfyUI"),
        venv_path=Path("D:/envs/env1/venv"),
        python_executable=Path("D:/envs/env1/venv/Scripts/python.exe"),
        custom_nodes_path=Path("D:/envs/env1/custom_nodes"),
        extra_model_paths_yaml=Path("D:/envs/env1/extra_model_paths.yaml"),
        port=8188,
        enabled_node_ids=[],
        status="stopped",
        pid=None,
    )
    defaults.update(overrides)
    return Environment(**defaults)
