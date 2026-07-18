"""M4: 3 处版本号必须一致。"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_python_version():
    sys.path.insert(0, str(ROOT / "src"))
    import comfy_mgr
    assert comfy_mgr.__version__ == "0.6.3"


def test_errors_json_version():
    data = json.loads((ROOT / "shared" / "errors.json").read_text(
        encoding="utf-8"))
    assert data["_version"] == "0.6.3"


def test_csproj_version():
    csproj = (ROOT / "src-wpf" / "ComfyUI.Manager" /
              "ComfyUI.Manager.csproj").read_text(encoding="utf-8")
    m = re.search(r"<Version>([^<]+)</Version>", csproj)
    assert m is not None
    assert m.group(1) == "0.6.3"
