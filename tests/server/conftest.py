"""Server 路由测试共享 fixture。

提供:
- `event_bus`:EventBus 实例(路由测试不直接用,但保留以与 brief 一致)
- `mock_env_bridge` / `mock_catalog_bridge` / ...:各 bridge 的 MagicMock
- `client`:FastAPI TestClient,app.state.<bridge> 已注入 mock
"""
from __future__ import annotations
from unittest.mock import MagicMock

import pytest
from fastapi.testclient import TestClient


# === Bridge mocks ===

@pytest.fixture
def event_bus():
    from comfy_mgr.infra.event_bus import EventBus
    return EventBus()


@pytest.fixture
def mock_env_bridge():
    return MagicMock()


@pytest.fixture
def mock_catalog_bridge():
    return MagicMock()


@pytest.fixture
def mock_process_bridge():
    return MagicMock()


@pytest.fixture
def mock_settings_bridge():
    bridge = MagicMock()
    bridge.current = {"theme": "light"}
    return bridge


@pytest.fixture
def mock_torch_bridge():
    bridge = MagicMock()
    bridge.suggested_cu_versions = ["cu118", "cu121", "cu124", "cpu"]
    return bridge


# === TestClient + 已注入 mock bridge 的 app ===

@pytest.fixture
def client(
    mock_env_bridge,
    mock_catalog_bridge,
    mock_process_bridge,
    mock_settings_bridge,
    mock_torch_bridge,
):
    """FastAPI TestClient with all 5 bridge mocks injected into app.state.

    `build_app` lifespan 会调 `recover_persisted_processes(ctx)`,所以我们传入
    一个 mock ctx,把它需要 list 的 repo 全部设为 `[]`。
    """
    from comfy_mgr.server.app import build_app

    ctx = MagicMock()
    # recover_persisted_processes 需要可迭代的 list_all()
    ctx.process._state_repo.list_all.return_value = []
    ctx.environment.list_all.return_value = []
    # ctx.environment.get 也可能用到(给个默认 mock)
    ctx.environment.get.return_value = None

    app = build_app(ctx)
    app.state.environment_bridge = mock_env_bridge
    app.state.catalog_bridge = mock_catalog_bridge
    app.state.process_bridge = mock_process_bridge
    app.state.settings_bridge = mock_settings_bridge
    app.state.torch_bridge = mock_torch_bridge

    return TestClient(app)
