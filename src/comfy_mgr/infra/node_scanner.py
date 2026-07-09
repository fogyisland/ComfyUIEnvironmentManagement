"""NodeScanner:AST 解析 custom_node __init__.py,提取 NODE_CLASS_MAPPINGS。

4 层降级(M4 新增 IMPORT_FALLBACK):
  - AST_CLEAN:        字面量 dict 完整提取
  - AST_DYNAMIC:      函数调用 / ** 展开 / 变量引用,放弃 AST
  - IMPORT_FALLBACK:  AST_DYNAMIC 后,importlib 实际加载模块,读 module.NODE_CLASS_MAPPINGS
  - NOT_FOUND:        仍没找到
  - PARSE_ERROR:      语法错(无法 fallback)
"""
from __future__ import annotations
import ast
import importlib.util
import sys
from enum import Enum
from pathlib import Path


class ScanSource(str, Enum):
    AST_CLEAN = "ast_clean"
    AST_DYNAMIC = "ast_dynamic"
    IMPORT_FALLBACK = "import_fallback"  # M4 新增
    NOT_FOUND = "not_found"
    PARSE_ERROR = "parse_error"


class NodeScanner:
    """纯函数式解析,无副作用,易测。"""

    def extract_class_mappings(
        self, init_py: Path,
    ) -> tuple[list[str], ScanSource, list[str]]:
        warnings: list[str] = []
        try:
            src = init_py.read_text(encoding="utf-8", errors="ignore")
        except OSError as e:
            return [], ScanSource.NOT_FOUND, [f"read_failed: {e}"]

        try:
            tree = ast.parse(src)
        except SyntaxError as e:
            return [], ScanSource.PARSE_ERROR, [f"syntax_error: line {e.lineno}"]

        for node in ast.walk(tree):
            if not isinstance(node, ast.Assign):
                continue
            for target in node.targets:
                if not (isinstance(target, ast.Name)
                        and target.id == "NODE_CLASS_MAPPINGS"):
                    continue
                if isinstance(node.value, ast.Dict):
                    if any(k is None for k in node.value.keys):
                        # AST_DYNAMIC — 尝试 importlib 兜底
                        keys, fallback_warnings = self._import_fallback(init_py)
                        if keys is not None:
                            warnings.extend(fallback_warnings)
                            return keys, ScanSource.IMPORT_FALLBACK, warnings
                        return [], ScanSource.AST_DYNAMIC, ["dynamic_mappings"]
                    keys = [
                        k.value for k in node.value.keys
                        if isinstance(k, ast.Constant)
                        and isinstance(k.value, str)
                    ]
                    return keys, ScanSource.AST_CLEAN, warnings
                # 其他形式(Call / Name 引用等)→ AST_DYNAMIC → importlib 兜底
                keys, fallback_warnings = self._import_fallback(init_py)
                if keys is not None:
                    warnings.extend(fallback_warnings)
                    return keys, ScanSource.IMPORT_FALLBACK, warnings
                return [], ScanSource.AST_DYNAMIC, ["dynamic_mappings"]

        return [], ScanSource.NOT_FOUND, ["mappings_not_found"]

    def _import_fallback(self, init_py: Path) -> tuple[list[str] | None, list[str]]:
        """importlib 加载 init_py,读 module.NODE_CLASS_MAPPINGS。

        Returns:
            (keys, warnings):keys 为 None 表示 fallback 失败。
        """
        warnings: list[str] = []
        try:
            spec = importlib.util.spec_from_file_location(
                f"_scan_fallback_{init_py.parent.name}", str(init_py))
            if spec is None or spec.loader is None:
                return None, ["fallback_no_spec"]
            mod = importlib.util.module_from_spec(spec)
            sys.modules[spec.name] = mod
            try:
                spec.loader.exec_module(mod)
            except Exception as e:
                return None, [f"fallback_exec_failed: {e}"]
            finally:
                sys.modules.pop(spec.name, None)
            mappings = getattr(mod, "NODE_CLASS_MAPPINGS", None)
            if isinstance(mappings, dict):
                keys = [str(k) for k in mappings.keys()]
                warnings.append("fallback_used: importlib")
                return keys, warnings
            return None, ["fallback_no_mappings"]
        except Exception as e:
            return None, [f"fallback_error: {e}"]