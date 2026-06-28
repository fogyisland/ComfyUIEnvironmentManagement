"""CompatHTTPClient:外部冲突知识库查询(M3 留 hook)。"""
from __future__ import annotations
from typing import TypedDict
from comfy_mgr.infra.http_client import HTTPClient
from comfy_mgr.result import Result, ServiceError


class DepSpec(TypedDict, total=False):
    name: str
    spec: str | None


class CompatHTTPClient:
    def __init__(self, *, base_url: str, http_client: HTTPClient):
        self.base_url = base_url.rstrip("/")
        self.http = http_client

    def check_known_incompat(self, deps: list[DepSpec]) -> Result[list[dict]]:
        if not self.base_url:
            return Result.fail(ServiceError(
                code="API_NOT_CONFIGURED",
                message="compat_api_base_url 未配置",
            ))
        url = f"{self.base_url}/compat/check"
        return self.http.post(url, json_body={"deps": deps})
