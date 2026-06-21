from comfy_mgr.result import Result, ServiceError

def test_ok_creates_success_result():
    r = Result.ok("hello")
    assert r.ok is True
    assert r.value == "hello"
    assert r.error is None

def test_fail_creates_failure_result():
    err = ServiceError(code="X", message="bad", recoverable=True)
    r = Result.fail(err)
    assert r.ok is False
    assert r.value is None
    assert r.error is err

def test_result_generic_value_type():
    r: Result[int] = Result.ok(42)
    assert r.value == 42
