"""CompatHTTPClient:M3 阶段 base_url 为空 → API_NOT_CONFIGURED。"""
from comfy_mgr.infra.compat_http_client import CompatHTTPClient


class MockHTTPClient:
    def __init__(self):
        self.calls = []
    def post(self, url, *, json_body=None):
        from comfy_mgr.result import Result
        self.calls.append(url)
        return Result.ok({"incompat": []})


def test_check_returns_not_configured_when_base_url_empty():
    http = MockHTTPClient()
    client = CompatHTTPClient(base_url="", http_client=http)
    r = client.check_known_incompat([{"name": "torch", "spec": ">=2.0"}])
    assert not r.ok
    assert r.error.code == "API_NOT_CONFIGURED"
    assert http.calls == []


def test_check_calls_http_when_configured():
    http = MockHTTPClient()
    client = CompatHTTPClient(
        base_url="https://compat.example.org", http_client=http,
    )
    r = client.check_known_incompat([{"name": "torch", "spec": ">=2.0"}])
    assert r.ok
    assert http.calls == ["https://compat.example.org/compat/check"]
