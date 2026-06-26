"""NodeScanner:AST 解析 custom_node __init__.py,提取 NODE_CLASS_MAPPINGS。

3 层降级:
  - AST_CLEAN:    字面量 dict 完整提取
  - AST_DYNAMIC:  函数调用 / ** 展开 / 变量引用,放弃
  - NOT_FOUND:    没找到 NODE_CLASS_MAPPINGS
  - PARSE_ERROR:  语法错
"""
from __future__ import annotations
import ast
from enum import Enum
from pathlib import Path


class ScanSource(str, Enum):
    AST_CLEAN = "ast_clean"
    AST_DYNAMIC = "ast_dynamic"
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
                # 命中 NODE_CLASS_MAPPINGS = ...
                if isinstance(node.value, ast.Dict):
                    # 出现 ** 展开 → 视为 dynamic(无法静态知道会注入哪些 key)
                    # Py3.10+ 的 AST 中,**x 在 dict 里表示为 key=None
                    if any(k is None for k in node.value.keys):
                        return [], ScanSource.AST_DYNAMIC, ["dynamic_mappings"]
                    keys = [
                        k.value for k in node.value.keys
                        if isinstance(k, ast.Constant)
                        and isinstance(k.value, str)
                    ]
                    return keys, ScanSource.AST_CLEAN, warnings
                # 其他形式:Call(函数调用)、** 展开(Name 引用)、Name(变量引用)等
                return [], ScanSource.AST_DYNAMIC, ["dynamic_mappings"]

        return [], ScanSource.NOT_FOUND, ["mappings_not_found"]
