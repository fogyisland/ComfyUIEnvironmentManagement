"""HTTPClient:urllib + UA + 错误分类(4xx 不重试,5xx 重试)。"""
from unittest.mock import patch, MagicMock
from comfy_mgr.infra.http_client import HTTPClient


def _mock_resp(status=200, body=b'{"x":1}', headers=None):
    r = MagicMock()
    r.status = status
    r.read.return_value = body
    r.headers = headers or {"Content-Type": "application/json"}
    return r


def test_get_success_json():
    client = HTTPClient(max_retries=0)
    with patch.object(client, "_opener") as mock_op:
        mock_op.open.return_value = _mock_resp(200, b'{"x":1}')
        r = client.get("https://x.test/api")
    assert r.ok
    assert r.value == {"x": 1}


def test_get_success_bytes():
    client = HTTPClient(max_retries=0)
    with patch.object(client, "_opener") as mock_op:
        mock_op.open.return_value = _mock_resp(200, b"raw", {"Content-Type": "text/plain"})
        r = client.get("https://x.test/file")
    assert r.ok
    assert r.value == b"raw"


def test_get_4xx_no_retry():
    client = HTTPClient(max_retries=3)
    with patch.object(client, "_opener") as mock_op:
        mock_op.open.return_value = _mock_resp(404, b"not found")
        r = client.get("https://x.test/missing")
    assert not r.ok
    assert r.error.code == "HTTP_STATUS_4XX"
    assert mock_op.open.call_count == 1  # 不重试


def test_get_429_with_retry_after_triggers_retry():
    """429 即使 4xx 也重试(等 Retry-After header)。"""
    client = HTTPClient(max_retries=3, backoff_base=0.001)
    headers = {"Retry-After": "0"}
    with patch.object(client, "_opener") as mock_op:
        # 第一次 429,第二次 200
        mock_op.open.side_effect = [
            _mock_resp(429, b"", headers),
            _mock_resp(200, b'{"ok":1}'),
        ]
        with patch("time.sleep") as mock_sleep:
            r = client.get("https://x.test/x")
    assert r.ok
    assert mock_op.open.call_count == 2
    mock_sleep.assert_called()  # 至少 sleep 一次


def test_get_5xx_retries_then_fails():
    client = HTTPClient(max_retries=2, backoff_base=0.001)
    with patch.object(client, "_opener") as mock_op:
        mock_op.open.return_value = _mock_resp(503, b"down")
        with patch("time.sleep"):
            r = client.get("https://x.test/x")
    assert not r.ok
    assert r.error.code == "HTTP_STATUS_5XX"
    assert mock_op.open.call_count == 3  # 1 + 2 retries


def test_get_timeout_retries():
    from urllib.error import URLError
    client = HTTPClient(max_retries=2, backoff_base=0.001)
    with patch.object(client, "_opener") as mock_op:
        mock_op.open.side_effect = URLError("timed out")
        with patch("time.sleep"):
            r = client.get("https://x.test/x")
    assert not r.ok
    assert r.error.code == "HTTP_TIMEOUT"
    assert mock_op.open.call_count == 3


def test_get_sends_user_agent():
    client = HTTPClient(max_retries=0, user_agent="Test/1.0")
    captured_req = []
    def capture(req, **kw):
        captured_req.append(req)
        return _mock_resp(200, b'{}')
    with patch.object(client, "_opener") as mock_op:
        mock_op.open.side_effect = capture
        client.get("https://x.test/x")
    assert "Test/1.0" in captured_req[0].get_header("User-agent")


def test_post_json():
    import json
    client = HTTPClient(max_retries=0)
    with patch.object(client, "_opener") as mock_op:
        mock_op.open.return_value = _mock_resp(200, b'{"ok":1}')
        r = client.post("https://x.test/x", json_body={"a": 1})
    assert r.ok
    req = mock_op.open.call_args[0][0]
    body = req.data.decode("utf-8")
    assert json.loads(body) == {"a": 1}


def test_head_returns_status():
    client = HTTPClient(max_retries=0)
    with patch.object(client, "_opener") as mock_op:
        mock_op.open.return_value = _mock_resp(200, b"")
        r = client.head("https://x.test/x")
    assert r.ok
    assert r.value == 200
