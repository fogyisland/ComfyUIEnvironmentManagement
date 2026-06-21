import pytest
from pathlib import Path

PROJECT_ROOT = Path(__file__).parent.parent
TEMPLATE_DIR = PROJECT_ROOT / "template"

@pytest.fixture
def tmp_appdata(monkeypatch, tmp_path):
    """重定向 APPDATA 到临时目录，settings 和 db 都在这里。"""
    appdata = tmp_path / "appdata"
    appdata.mkdir()
    monkeypatch.setenv("APPDATA", str(appdata))
    return appdata

@pytest.fixture
def template_python():
    """返回 template/ 下第一个可用的 python.exe。"""
    if not TEMPLATE_DIR.exists():
        pytest.skip("template/ 目录不存在")
    for ver in ["3.10", "3.11", "3.12", "3.13", "3.14"]:
        p = TEMPLATE_DIR / ver / "python.exe"
        if p.exists():
            return p
    pytest.skip("template/ 下没有可用的 python.exe")


@pytest.fixture
def platform_mock(monkeypatch):
    """用于在 Windows 上模拟其他平台。"""
    return monkeypatch
