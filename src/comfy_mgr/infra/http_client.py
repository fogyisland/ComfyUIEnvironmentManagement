"""HTTPClient:urllib + 重试 + UA + JSON/bytes 自动判断。

不引入 requests/httpx,沿用 M2 GitHubClient 的 urllib 风格。
重试策略(spec §4.6):
  - 5xx / timeout / connection error → 重试(指数退避 1s/2s/4s,最多 max_retries)
  - 4xx 除 429 → 不重试
  - 429 → 重试(等 Retry-After header)
  - JSON parse 失败 → 不重试
"""
from __future__ import annotations
import json
import time
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, build_opener

from comfy_mgr.result import Result, ServiceError

DEFAULT_USER_AGENT = (
    "ComfyUI-Manager/0.3.0 (+https://github.com/fogyisland/ComfyUIEnvironmentManagement)"
)


class HTTPClient:
    def __init__(
        self,
        *,
        timeout: float = 10.0,
        max_retries: int = 3,
        backoff_base: float = 1.0,
        user_agent: str = DEFAULT_USER_AGENT,
    ):
        self.timeout = timeout
        self.max_retries = max_retries
        self.backoff_base = backoff_base
        self.user_agent = user_agent
        self._opener = build_opener()

    # ----- public API -----

    def get(self, url: str, *, params: dict | None = None) -> Result[Any]:
        if params:
            from urllib.parse import urlencode
            url = f"{url}?{urlencode(params)}"
        return self._request("GET", url)

    def post(self, url: str, *, json_body: dict | None = None) -> Result[Any]:
        data = json.dumps(json_body).encode("utf-8") if json_body else b""
        return self._request("POST", url, data=data,
                             extra_headers={"Content-Type": "application/json"})

    def head(self, url: str) -> Result[int]:
        return self._request("HEAD", url, want_status_only=True)

    # ----- core -----

    def _request(
        self, method: str, url: str, *,
        data: bytes | None = None,
        extra_headers: dict | None = None,
        want_status_only: bool = False,
    ) -> Result[Any]:
        headers = {"User-Agent": self.user_agent}
        if extra_headers:
            headers.update(extra_headers)
        req = Request(url, data=data, method=method, headers=headers)

        last_error: ServiceError | None = None
        for attempt in range(self.max_retries + 1):
            try:
                resp = self._opener.open(req, timeout=self.timeout)
                # urllib 正常情况会把 4xx/5xx 转成 HTTPError,这里兜底处理直接返回错误对象的情况
                code = getattr(resp, "status", 200)
                if 400 <= code < 500 and code != 429:
                    body = resp.read()[:200] if hasattr(resp, "read") else b""
                    return Result.fail(ServiceError(
                        code="HTTP_STATUS_4XX",
                        message=f"HTTP {code}",
                        detail={"body": body.decode("utf-8", "replace")},
                    ))
                if code == 429:
                    hdrs = getattr(resp, "headers", {}) or {}
                    retry_after = float(hdrs.get("Retry-After", 1))
                    last_error = ServiceError(code="HTTP_STATUS_429",
                                              message="rate limited")
                    if attempt < self.max_retries:
                        time.sleep(retry_after)
                        continue
                    break
                if code >= 500:
                    body = resp.read()[:200] if hasattr(resp, "read") else b""
                    last_error = ServiceError(
                        code="HTTP_STATUS_5XX",
                        message=f"HTTP {code}",
                        detail={"body": body.decode("utf-8", "replace")},
                    )
                    if attempt < self.max_retries:
                        time.sleep(self.backoff_base * (2 ** attempt))
                        continue
                    break
            except HTTPError as e:
                # 4xx / 5xx
                code = e.code
                body = e.read()[:200] if hasattr(e, "read") else b""
                if 400 <= code < 500 and code != 429:
                    return Result.fail(ServiceError(
                        code="HTTP_STATUS_4XX",
                        message=f"HTTP {code}",
                        detail={"body": body.decode("utf-8", "replace")},
                    ))
                if code == 429:
                    # 等 Retry-After 后重试
                    retry_after = float(e.headers.get("Retry-After", 1))
                    last_error = ServiceError(code="HTTP_STATUS_429",
                                              message="rate limited")
                    if attempt < self.max_retries:
                        time.sleep(retry_after)
                        continue
                else:  # 5xx
                    last_error = ServiceError(
                        code="HTTP_STATUS_5XX",
                        message=f"HTTP {code}",
                        detail={"body": body.decode("utf-8", "replace")},
                    )
                    if attempt < self.max_retries:
                        time.sleep(self.backoff_base * (2 ** attempt))
                        continue
            except URLError as e:
                # timeout / DNS / connection refused
                reason = str(e.reason) if hasattr(e, "reason") else str(e)
                last_error = ServiceError(
                    code="HTTP_TIMEOUT" if "timed out" in reason.lower()
                         else "HTTP_CONNECTION_FAILED",
                    message=reason,
                )
                if attempt < self.max_retries:
                    time.sleep(self.backoff_base * (2 ** attempt))
                    continue
                break
            except Exception as e:
                return Result.fail(ServiceError(
                    code="HTTP_FAILED", message=str(e)))

            # 成功分支
            try:
                if want_status_only:
                    return Result.ok(resp.status)
                body = resp.read()
                ctype = resp.headers.get("Content-Type", "")
                if "application/json" in ctype:
                    return Result.ok(json.loads(body.decode("utf-8")))
                return Result.ok(body)
            except Exception as e:
                return Result.fail(ServiceError(
                    code="HTTP_PARSE_FAILED", message=str(e)))

        # 重试耗尽
        return Result.fail(last_error or ServiceError(
            code="HTTP_FAILED", message="retry exhausted"))
