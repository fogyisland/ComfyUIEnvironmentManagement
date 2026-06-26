"""NodeMetaService:GitHub 元数据缓存,1h TTL。"""
from __future__ import annotations
import sqlite3
from datetime import datetime, timezone
from typing import Optional

from comfy_mgr.db.node_meta_repo import NodeMetaRepo
from comfy_mgr.infra.github_client import GitHubClient
from comfy_mgr.infra.pkg_meta import _now_iso
from comfy_mgr.models.node_meta import NodeMeta
from comfy_mgr.result import Result, ServiceError


class NodeMetaService:
    def __init__(
        self,
        conn: sqlite3.Connection,
        github: GitHubClient,
        cache_ttl_seconds: int = 3600,
    ):
        self.conn = conn
        self.github = github
        self.cache_ttl_seconds = cache_ttl_seconds
        self.repo = NodeMetaRepo(conn)

    def get_cached(self, package: str) -> Result[Optional[NodeMeta]]:
        return Result.ok(self.repo.get(package))

    def fetch(self, package: str, owner: str, repo: str) -> Result[NodeMeta]:
        """强制刷新,失败也写 fetch_error 到缓存。"""
        r = self.github.get_repo_meta(owner, repo)
        if not r.ok:
            # 写 fetch_error 缓存
            self.repo.upsert(NodeMeta(
                package=package, fetched_at=_now_iso(),
                fetch_error=r.error.message,
            ))
            return r

        data = r.value
        meta = NodeMeta(
            package=package,
            github_url=data.get("github_url"),
            stars=data.get("stars"),
            last_commit=data.get("last_commit"),
            homepage=data.get("homepage"),
            fetched_at=_now_iso(),
            fetch_error=None,
        )
        self.repo.upsert(meta)
        return Result.ok(meta)

    def get_or_fetch(
        self, package: str, owner: str, repo: str,
    ) -> Result[NodeMeta]:
        cached = self.repo.get(package)
        if cached and not cached.fetch_error and self._is_fresh(cached):
            return Result.ok(cached)
        return self.fetch(package, owner, repo)

    def _is_fresh(self, meta: NodeMeta) -> bool:
        if not meta.fetched_at:
            return False
        try:
            ts = datetime.fromisoformat(meta.fetched_at)
        except ValueError:
            return False
        if ts.tzinfo is None:
            ts = ts.replace(tzinfo=timezone.utc)
        age = (datetime.now(timezone.utc) - ts).total_seconds()
        return age < self.cache_ttl_seconds
