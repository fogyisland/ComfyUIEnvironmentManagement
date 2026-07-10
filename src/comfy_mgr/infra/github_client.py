"""GitHubClient:urllib 实现的极简 GitHub API 客户端。"""
from __future__ import annotations
import json
import urllib.request
import urllib.error

from comfy_mgr.result import Result, ServiceError


class GitHubClient:
    """anonymous API,60 req/h 限制。失败不重试。"""

    def __init__(self, timeout: float = 10.0):
        self.timeout = timeout

    def get_repo_meta(self, owner: str, repo: str) -> Result[dict]:
        url = f"https://api.github.com/repos/{owner}/{repo}"
        try:
            with urllib.request.urlopen(url, timeout=self.timeout) as resp:
                raw = resp.read().decode("utf-8")
            data = json.loads(raw)
            return Result.ok({
                "stars": data.get("stargazers_count"),
                "last_commit": data.get("pushed_at"),
                "homepage": data.get("homepage"),
                "github_url": data.get("html_url"),
            })
        except (urllib.error.URLError, urllib.error.HTTPError,
                json.JSONDecodeError, OSError) as e:
            return Result.fail(ServiceError(
                code="META_FETCH_FAILED", message=str(e)))
