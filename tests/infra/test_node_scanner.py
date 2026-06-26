"""NodeScanner:AST 静态解析 NODE_CLASS_MAPPINGS。"""
from __future__ import annotations
from pathlib import Path
import pytest
from comfy_mgr.infra.node_scanner import NodeScanner, ScanSource


@pytest.fixture
def scanner() -> NodeScanner:
    return NodeScanner()


def test_extract_literal_dict(scanner, tmp_path: Path):
    f = tmp_path / "__init__.py"
    f.write_text('NODE_CLASS_MAPPINGS = {"KSampler": K, "LatentUpscale": L}\n')
    classes, source, warnings = scanner.extract_class_mappings(f)
    assert classes == ["KSampler", "LatentUpscale"]
    assert source == ScanSource.AST_CLEAN
    assert warnings == []


def test_extract_empty_literal_dict(scanner, tmp_path: Path):
    f = tmp_path / "__init__.py"
    f.write_text('NODE_CLASS_MAPPINGS = {}\n')
    classes, source, _ = scanner.extract_class_mappings(f)
    assert classes == []
    assert source == ScanSource.AST_CLEAN


def test_extract_dict_with_non_string_keys(scanner, tmp_path: Path):
    f = tmp_path / "__init__.py"
    # ast 应该会跳过非 string key(Mypy / 内部 const 之类)
    f.write_text('NODE_CLASS_MAPPINGS = {42: "x", "OK": "y"}\n')
    classes, source, _ = scanner.extract_class_mappings(f)
    assert classes == ["OK"]


def test_extract_dynamic_call(scanner, tmp_path: Path):
    f = tmp_path / "__init__.py"
    f.write_text('NODE_CLASS_MAPPINGS = build_mappings()\n')
    classes, source, warnings = scanner.extract_class_mappings(f)
    assert classes == []
    assert source == ScanSource.AST_DYNAMIC
    assert "dynamic_mappings" in warnings


def test_extract_dict_unpack(scanner, tmp_path: Path):
    f = tmp_path / "__init__.py"
    f.write_text('NODE_CLASS_MAPPINGS = {**BASE, "Extra": Extra}\n')
    classes, source, warnings = scanner.extract_class_mappings(f)
    assert classes == []
    assert source == ScanSource.AST_DYNAMIC


def test_extract_not_found(scanner, tmp_path: Path):
    f = tmp_path / "__init__.py"
    f.write_text('SOMETHING_ELSE = {"A": A}\n')
    classes, source, warnings = scanner.extract_class_mappings(f)
    assert classes == []
    assert source == ScanSource.NOT_FOUND
    assert "mappings_not_found" in warnings


def test_extract_syntax_error(scanner, tmp_path: Path):
    f = tmp_path / "__init__.py"
    f.write_text('def x(:\n')  # 语法错
    classes, source, warnings = scanner.extract_class_mappings(f)
    assert classes == []
    assert source == ScanSource.PARSE_ERROR
    assert any("syntax_error" in w for w in warnings)


def test_extract_file_not_found(scanner, tmp_path: Path):
    f = tmp_path / "missing.py"
    classes, source, warnings = scanner.extract_class_mappings(f)
    assert classes == []
    assert source == ScanSource.NOT_FOUND
    assert any("read_failed" in w for w in warnings)
