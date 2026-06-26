"""pkg_meta helpers 测试。"""
from __future__ import annotations
from pathlib import Path
import json
import re
import time
from comfy_mgr.infra.pkg_meta import (
    _parse_pyproject, _placeholder_node, _now_iso,
    PkgMeta,
)


# ---------------- _parse_pyproject ----------------

def test_parse_pyproject_full(tmp_path: Path):
    pkg = tmp_path / "pkg"
    pkg.mkdir()
    (pkg / "pyproject.toml").write_text(
        '[project]\n'
        'name = "ComfyUI-Impact-Pack"\n'
        'version = "7.0.0"\n'
        'description = "Impact pack for ComfyUI"\n'
        'authors = [{name = "Dr.Lt.Data"}]\n'
    )
    meta = _parse_pyproject(pkg)
    assert meta.name == "ComfyUI-Impact-Pack"
    assert meta.version == "7.0.0"
    assert meta.description == "Impact pack for ComfyUI"
    assert meta.author == "Dr.Lt.Data"


def test_parse_pyproject_no_file(tmp_path: Path):
    pkg = tmp_path / "pkg"
    pkg.mkdir()
    meta = _parse_pyproject(pkg)
    assert meta.name == ""
    assert meta.version is None
    assert meta.author is None


def test_parse_pyproject_malformed(tmp_path: Path):
    pkg = tmp_path / "pkg"
    pkg.mkdir()
    (pkg / "pyproject.toml").write_text("not valid toml [[[\n")
    meta = _parse_pyproject(pkg)
    # 解析失败应当 fall back 到空,不抛
    assert meta.name == ""


def test_parse_pyproject_pep621_authors_as_list(tmp_path: Path):
    pkg = tmp_path / "pkg"
    pkg.mkdir()
    (pkg / "pyproject.toml").write_text(
        '[project]\n'
        'name = "x"\n'
        'authors = [\n'
        '  {name = "Alice"},\n'
        '  {name = "Bob"},\n'
        ']\n'
    )
    meta = _parse_pyproject(pkg)
    assert meta.author == "Alice"


# ---------------- _placeholder_node ----------------

def test_placeholder_node_fills_defaults(tmp_path: Path):
    pkg = tmp_path / "pkg-bad"
    pkg.mkdir()
    node = _placeholder_node("env-1", pkg, "boom")
    assert node.env_id == "env-1"
    assert node.package == "pkg-bad"
    assert str(node.package_path) == str(pkg)
    assert node.class_mappings == []
    assert node.status == "enabled"
    assert node.scan_meta["source"] == "not_found"
    assert "boom" in node.scan_meta["warnings"]


# ---------------- _now_iso ----------------

def test_now_iso_format():
    s = _now_iso()
    # 2026-06-26T12:34:56+00:00
    assert re.match(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", s)


def test_now_iso_is_utc():
    a = _now_iso()
    time.sleep(1.1)
    b = _now_iso()
    assert a < b
