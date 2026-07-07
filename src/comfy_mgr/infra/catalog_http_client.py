"""CatalogHTTPClient:远程节点目录 + 本地 cache + 离线降级。

策略:
  - list_remote:  缓存未过期 → 返回 cache;否则 HTTP GET,失败 → stale cache
  - search_remote: 仅本地 cache 内 substring,不发起 HTTP
  - get_remote:   按 package 查单条,失败 → stale cache
"""
from __future__ import annotations
import hashlib
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

    def list_remote(self, *, force_refresh: bool = False,
                    progress_cb=None) -> Result[list[dict]]:
        if not force_refresh:
            cached = self.repo.list_non_expired()
            if cached:
                return Result.ok([self._to_dict(r) for r in cached])
        # HTTP 拉 — 全量翻页 + 并发加速
        all_entries: list[dict] = []
        page_size = 200
        # 先拉第 1 页拿到 total(若有),否则按 50 页上限
        first_r = self.http.get(self.source_url, params={"limit": page_size, "offset": 0})
        if not first_r.ok:
            stale = self.repo.list_all()
            return Result.ok([self._to_dict(s, stale=True) for s in stale])
        first_page = self._extract_entries(first_r.value)
        all_entries.extend(first_page)
        total_known = isinstance(first_r.value, dict) and "total" in first_r.value
        total = first_r.value.get("total") if total_known else None
        max_pages_remaining = 50 if total_known else 200  # 无 total 时按 50 页
        if total_known and progress_cb:
            progress_cb(len(first_page), total)
        # 并发拉剩余页
        from concurrent.futures import ThreadPoolExecutor, as_completed
        page_offsets = [(i + 1) * page_size for i in range(max_pages_remaining)]
        # 终止条件:已超过 total
        if total_known:
            page_offsets = [o for o in page_offsets if o < total]
        if not page_offsets:
            pass  # 只有 1 页
        else:
            with ThreadPoolExecutor(max_workers=4) as ex:
                future_to_offset = {
                    ex.submit(self.http.get, self.source_url,
                              params={"limit": page_size, "offset": off}): off
                    for off in page_offsets
                }
                for fut in as_completed(future_to_offset):
                    r = fut.result()
                    if not r.ok:
                        continue
                    page = self._extract_entries(r.value)
                    all_entries.extend(page)
                    if progress_cb:
                        progress_cb(len(all_entries), total)
                    # 返回数量 < page_size → 终止
                    if len(page) < page_size:
                        # 取消剩余
                        for f in future_to_offset:
                            f.cancel()
                        break
        # 清掉旧 cache(避免 stale 数据)后写新 cache
        self.repo.delete_all_for_source(self.source_url)
        now = datetime.now()
        expires_at = (now + timedelta(seconds=self.cache_ttl_seconds)).isoformat(timespec="seconds")
        now_iso = now.isoformat(timespec="seconds")
        for entry in all_entries:
            pkg = entry.get("id") or entry.get("name", "")
            if not pkg:
                continue
            # id 必须 unique — 用 hash(source_url + pkg) 避免 pkg 重名时碰撞,
            # 也避免之前 "cc-<pkg>" 格式在翻页场景下互相覆盖
            digest = hashlib.sha1(f"{self.source_url}|{pkg}".encode()).hexdigest()[:12]
            self.repo.upsert({
                "id": f"cc-{digest}",
                "source_url": self.source_url,
                "package": pkg,
                "raw_metadata": json.dumps(entry),
                "cached_at": now_iso,
                "expires_at": expires_at,
            })
        return Result.ok(all_entries)

    @staticmethod
    def _extract_entries(value) -> list[dict]:
        # 适配多种 schema:
        #  - api.comfy.org/nodes: {"limit": N, "nodes": [...]}
        #  - 直数组: [...]
        #  - 其他: {"items": [...]}, {"data": [...]}
        if isinstance(value, list):
            return value
        if isinstance(value, dict):
            for key in ("nodes", "items", "data", "results", "entries"):
                v = value.get(key)
                if isinstance(v, list):
                    return v
        return []

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
        entry = self._extract_entries(r.value) if not isinstance(r.value, dict) else r.value
        entry = entry[0] if isinstance(entry, list) and entry else (entry if isinstance(entry, dict) else {})
        digest = hashlib.sha1(f"{self.source_url}|{package}".encode()).hexdigest()[:12]
        self.repo.upsert({
            "id": f"cc-{digest}",
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
