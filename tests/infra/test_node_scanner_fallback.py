"""M4 T22: NodeScanner IMPORT_FALLBACK 兜底。"""
from __future__ import annotations
import pytest
from pathlib import Path
from comfy_mgr.infra.node_scanner import NodeScanner, ScanSource


@pytest.fixture
def scanner():
    return NodeScanner()


def test_ast_dynamic_with_fallback(scanner, tmp_path):
    """AST 看到 ** 展开 → 尝试 importlib 兜底。"""
    pkg = tmp_path / "pkg-fallback"
    pkg.mkdir()
    (pkg / "__init__.py").write_text(
        "A = type('A', (), {})\n"
        "B = type('B', (), {})\n"
        "NODE_CLASS_MAPPINGS = {**{'A': A, 'B': B}}\n"
    )
    keys, source, warnings = scanner.extract_class_mappings(
        pkg / "__init__.py")
    assert source == ScanSource.IMPORT_FALLBACK
    assert set(keys) == {"A", "B"}
    assert any("fallback_used" in w for w in warnings)


def test_ast_dynamic_fallback_fails(scanner, tmp_path):
    """AST 看到 ** 展开,importlib 也加载失败 → AST_DYNAMIC。"""
    pkg = tmp_path / "pkg-broken"
    pkg.mkdir()
    (pkg / "__init__.py").write_text(
        "import does_not_exist\n"
        "NODE_CLASS_MAPPINGS = {**{'A': object}}\n"
    )
    keys, source, warnings = scanner.extract_class_mappings(
        pkg / "__init__.py")
    assert source == ScanSource.AST_DYNAMIC
    assert keys == []


def test_call_expression_triggers_fallback(scanner, tmp_path):
    """NODE_CLASS_MAPPINGS = build_mappings() → importlib 兜底。"""
    pkg = tmp_path / "pkg-call"
    pkg.mkdir()
    (pkg / "__init__.py").write_text(
        "def build_mappings():\n"
        "    return {'X': object, 'Y': object}\n"
        "NODE_CLASS_MAPPINGS = build_mappings()\n"
    )
    keys, source, warnings = scanner.extract_class_mappings(
        pkg / "__init__.py")
    assert source == ScanSource.IMPORT_FALLBACK
    assert set(keys) == {"X", "Y"}


def test_ast_clean_still_works(scanner, tmp_path):
    """AST_CLEAN 路径不应被 fallback 干扰。"""
    pkg = tmp_path / "pkg-clean"
    pkg.mkdir()
    (pkg / "__init__.py").write_text(
        "NODE_CLASS_MAPPINGS = {'A': object}\n")
    keys, source, warnings = scanner.extract_class_mappings(
        pkg / "__init__.py")
    assert source == ScanSource.AST_CLEAN
    assert keys == ["A"]