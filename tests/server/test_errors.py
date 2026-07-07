"""shared/errors.json loader tests。"""
from comfy_mgr.server.errors import load_errors, get_error, classify_severity, format_message


def test_load_errors_has_version():
    data = load_errors()
    assert data["_version"] == "0.4.0"


def test_get_error_returns_info():
    info = get_error("ENV_NOT_FOUND")
    assert info["severity"] == "warn"
    assert info["http_status"] == 404


def test_classify_severity_known_code():
    assert classify_severity("DB_CORRUPTED") == "critical"
    assert classify_severity("PORT_IN_USE") == "error"
    assert classify_severity("LOCKED_VERSION") == "warn"


def test_classify_severity_unknown_defaults_to_warn():
    assert classify_severity("UNKNOWN_CODE") == "warn"


def test_format_message_zh():
    msg = format_message("ENV_NOT_FOUND", locale="zh_CN", env_id="env-1")
    assert msg == "环境 env-1 不存在"


def test_format_message_en():
    msg = format_message("ENV_NOT_FOUND", locale="en_US", env_id="env-1")
    assert msg == "Environment env-1 not found"


def test_format_message_missing_param_returns_template():
    msg = format_message("ENV_NOT_FOUND", locale="zh_CN")
    assert "env_id" in msg  # 保留模板,不抛错
