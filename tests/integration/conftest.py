"""集成测试共享 fixture。

- `app_and_client`: 真实 AppContext + tmp_path 的 DB + TestClient(全栈集成)。
  - 用 monkeypatch APPDATA 把 SettingsService 的 settings.json / catalog.db
    重定向到 tmp_path,实现隔离。
  - 同时把 `comfy_mgr.db.connection.get_connection` monkeypatch 成
    `check_same_thread=False`,因为 TestClient 的 lifespan 在另一个线程跑
    而 sqlite3 默认禁止跨线程使用同一 connection。

- `app_with_client`: 同上但返回 (app, client) 元组(便于 WS 测试访问 app.state)。
"""
from __future__ import annotations
import os
import sqlite3
from pathlib import Path

import pytest
from fastapi.testclient import TestClient


@pytest.fixture
def isolated_appdata(tmp_path, monkeypatch):
    """把 APPDATA 指向 tmp_path,SettingsService / 默认 DB 路径都跟着改。

    注意:必须在 import SettingsService / AppContext 之前 patch。
    """
    appdata = tmp_path / "appdata"
    appdata.mkdir()
    monkeypatch.setenv("APPDATA", str(appdata))
    return appdata


@pytest.fixture
def sqlite_cross_thread(monkeypatch):
    """允许 sqlite connection 跨线程使用,TestClient lifespan 需要。

    不修改源文件,只 monkeypatch `comfy_mgr.db.connection.get_connection`。
    """
    from comfy_mgr.db import connection as conn_module

    def patched(db_path: Path):
        db_path.parent.mkdir(parents=True, exist_ok=True)
        conn = sqlite3.connect(
            str(db_path), isolation_level=None, check_same_thread=False
        )
        conn.execute("PRAGMA journal_mode=WAL")
        conn.execute("PRAGMA foreign_keys=ON")
        conn.row_factory = sqlite3.Row
        return conn

    monkeypatch.setattr(conn_module, "get_connection", patched)


@pytest.fixture
def app_and_client(isolated_appdata, sqlite_cross_thread):
    """真实 AppContext + 隔离 DB + TestClient(全栈)。"""
    from comfy_mgr.app_context import AppContext
    from comfy_mgr.server.app import build_app

    ctx = AppContext()
    app = build_app(ctx)
    with TestClient(app) as client:
        yield app, client


@pytest.fixture
def app_with_client(isolated_appdata, sqlite_cross_thread):
    """同 app_and_client,但返回 (app, client) 元组。"""
    from comfy_mgr.app_context import AppContext
    from comfy_mgr.server.app import build_app

    ctx = AppContext()
    app = build_app(ctx)
    with TestClient(app) as client:
        yield app, client


@pytest.fixture
def app_only(isolated_appdata, sqlite_cross_thread):
    """只返回 FastAPI app(用于需要自己管理 lifespan 的测试)。"""
    from comfy_mgr.app_context import AppContext
    from comfy_mgr.server.app import build_app

    ctx = AppContext()
    return build_app(ctx)


@pytest.fixture
def ctx_only(isolated_appdata, sqlite_cross_thread):
    """只返回真实 AppContext(用于 lifespan / recover_persisted_processes 测试)。"""
    from comfy_mgr.app_context import AppContext
    return AppContext()