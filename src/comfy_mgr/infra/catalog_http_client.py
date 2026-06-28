"""CatalogHTTPClient:远程节点目录 + 本地 cache + 离线降级。

策略:
  - list_remote:  缓存未过期 → 返回 cache;否则 HTTP GET,失败 → stale cache
  - search_remote: 仅本地 cache 内 substring,不发起 HTTP
  - get_remote:   按 package 查单条,失败 → stale cache
"""
from __future__ import annotations
import json
from datetime import datetime, timedelta
from pathlib import Path
from urllib.parse import quote

from comfy_mgr.db.catalog_repo import CatalogCacheRepo
from comfy_mgr.infra.http_client import HTTPClient
from comfy_mgr.result import Result, ServiceError


class CatalogHTTPClient:
    def __init__(
        self,
        *,
        catalog_repo: CatalogCacheRepo,
        http_client: HTTPClient,
        base_url: str = "https://api.comfy.org",
        cache_ttl_seconds: int = 3600,
    ):
        self.repo = catalog_repo
        self.http = http_client
        self.base_url = base_url.rstrip("/")
        self.cache_ttl_seconds = cache_ttl_seconds
        self.source_url = f"{self.base_url}/nodes"

    # ----- list -----

    def list_remote(self, *, force_refresh: bool = False) -> Result[list[dict]]:
        if not force_refresh:
            cached = self.repo.list_non_expired()
            if cached:
                return Result.ok([self._to_dict(r) for r in cached])
        # HTTP 拉
        r = self.http.get(self.source_url)
        if not r.ok:
            # 离线降级:返回 stale cache(可能空)
            stale = self.repo.list_all()
            entries = [self._to_dict(s, stale=True) for s in stale]
            return Result.ok(entries)
        # 写 cache
        now = datetime.now()
        entries = r.value if isinstance(r.value, list) else []
        for entry in entries:
            pkg = entry.get("id") or entry.get("name", "")
            if not pkg:
                continue
            self.repo.upsert({
                "id": f"cc-{pkg}",
                "source_url": self.source_url,
                "package": pkg,
                "raw_metadata": json.dumps(entry),
                "cached_at": now.isoformat(timespec="seconds"),
                "expires_at": (now + timedelta(seconds=self.cache_ttl_seconds))
                    .isoformat(timespec="seconds"),
            })
        return Result.ok(entries)

    def search_remote(self, query: str, *, limit: int = 20) -> Result[list[dict]]:
        rows = self.repo.search_substring(query)
        return Result.ok([self._to_dict(r) for r in rows[:limit]])

    def get_remote(self, package: str, *, force_refresh: bool = False) -> Result[dict]:
        cached = self.repo.get_by_package(package)
        if cached and not force_refresh:
            expires = cached.get("expires_at", "")
            if expires > datetime.now().isoformat(timespec="seconds"):
                return Result.ok(self._to_dict(cached))
        # 试 HTTP 拉
        url = f"{self.source_url}/{quote(package, safe='')}"
        r = self.http.get(url)
        if not r.ok:
            if cached:
                return Result.ok(self._to_dict(cached, stale=True))
            return r
        now = datetime.now()
        entry = r.value if isinstance(r.value, dict) else {}
        self.repo.upsert({
            "id": f"cc-{package}",
            "source_url": self.source_url,
            "package": package,
            "raw_metadata": json.dumps(entry),
            "cached_at": now.isoformat(timespec="seconds"),
            "expires_at": (now + timedelta(seconds=self.cache_ttl_seconds))
                .isoformat(timespec="seconds"),
        })
        return Result.ok(entry)

    # ----- helpers -----

    @staticmethod
    def _to_dict(row: dict, *, stale: bool = False) -> dict:
        d = json.loads(row.get("raw_metadata", "{}"))
        if stale:
            d["stale"] = True
            d["cached_at"] = row.get("cached_at")
        return d
