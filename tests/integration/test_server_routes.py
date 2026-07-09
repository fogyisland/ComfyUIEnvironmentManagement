"""Server boot + REST 集成测试。"""
from __future__ import annotations
import pytest
from fastapi.testclient import TestClient


def test_healthz_returns_200(app_and_client):
    _, client = app_and_client
    r = client.get("/healthz")
    assert r.status_code == 200
    assert r.json() == {"status": "ok"}


def test_version_returns_schema_5(app_and_client):
    _, client = app_and_client
    r = client.get("/version")
    assert r.json()["schema"] == 5


def test_env_list_starts_empty(app_and_client):
    _, client = app_and_client
    r = client.post("/api/v1/env/list", json={"env_id": ""})
    assert r.status_code == 200
    assert r.json()["ok"] is True
    assert r.json()["value"] == []


def test_env_create_validates_required_fields(app_and_client):
    _, client = app_and_client
    r = client.post("/api/v1/env/create", json={"name": "x"})
    assert r.status_code == 422


def test_settings_get_all_returns_dict(app_and_client):
    _, client = app_and_client
    r = client.post("/api/v1/settings/get-all", json={})
    assert r.json()["ok"] is True
    assert "theme_mode" in r.json()["value"]


def test_env_get_for_unknown_env_returns_error(app_and_client):
    """env/get 不存在 → env_bridge 返回 ok=False envelope(错误码 ENV_NOT_FOUND)。"""
    _, client = app_and_client
    r = client.post("/api/v1/env/get", json={"env_id": "ghost"})
    assert r.status_code == 200
    body = r.json()
    assert body["ok"] is False
    assert body["error"]["code"] == "ENV_NOT_FOUND"


def test_unknown_route_returns_404(app_and_client):
    _, client = app_and_client
    r = client.post("/api/v1/foo/bar", json={})
    assert r.status_code == 404
    # FastAPI 默认 404 返回 {"detail": "Not Found"}
    assert "detail" in r.json()


def test_invalid_json_returns_422(app_and_client):
    """FastAPI 默认对非法 JSON 返回 422(不是 400)。"""
    _, client = app_and_client
    r = client.post(
        "/api/v1/env/list",
        data="not-json",
        headers={"content-type": "application/json"},
    )
    # pydantic 校验失败 = 422
    assert r.status_code == 422