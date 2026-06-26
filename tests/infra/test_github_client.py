"""GitHubClient:urllib 实现的 GitHub API 客户端。"""
from __future__ import annotations
from unittest.mock import patch, MagicMock
import json
import pytest
from comfy_mgr.infra.github_client import GitHubClient


@pytest.fixture
def client() -> GitHubClient:
    return GitHubClient(timeout=5)


def test_get_repo_meta_200(client):
    payload = {
        "stargazers_count": 100,
        "pushed_at": "2026-06-25T00:00:00Z",
        "homepage": "https://example.com",
        "html_url": "https://github.com/test/pkg",
    }
    fake_resp = MagicMock()
    fake_resp.read.return_value = json.dumps(payload).encode("utf-8")
    fake_resp.__enter__ = lambda s: s
    fake_resp.__exit__ = lambda s, *a: False

    with patch("urllib.request.urlopen", return_value=fake_resp):
        r = client.get_repo_meta("test", "pkg")

    assert r.ok
    assert r.value["stars"] == 100
    assert r.value["github_url"] == "https://github.com/test/pkg"
    assert r.value["last_commit"] == "2026-06-25T00:00:00Z"


def test_get_repo_meta_404(client):
    from urllib.error import HTTPError
    fake_resp = MagicMock()
    fake_resp.read.return_value = b"Not Found"
    err = HTTPError("https://api.github.com/repos/x/y", 404, "Not Found", {}, fake_resp)

    with patch("urllib.request.urlopen", side_effect=err):
        r = client.get_repo_meta("x", "y")

    assert not r.ok
    assert r.error.code == "META_FETCH_FAILED"


def test_get_repo_meta_network_error(client):
    from urllib.error import URLError
    with patch("urllib.request.urlopen",
               side_effect=URLError("network down")):
        r = client.get_repo_meta("x", "y")
    assert not r.ok
    assert r.error.code == "META_FETCH_FAILED"


def test_get_repo_meta_invalid_json(client):
    fake_resp = MagicMock()
    fake_resp.read.return_value = b"not json {"
    fake_resp.__enter__ = lambda s: s
    fake_resp.__exit__ = lambda s, *a: False

    with patch("urllib.request.urlopen", return_value=fake_resp):
        r = client.get_repo_meta("x", "y")
    assert not r.ok
    assert r.error.code == "META_FETCH_FAILED"
