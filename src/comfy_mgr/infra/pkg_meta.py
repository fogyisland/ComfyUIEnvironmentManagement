"""pkg_meta:per-package 元数据解析 + placeholder node 工厂 + ISO 时间 helper。

不引入新依赖:toml 解析用手写正则(够用 PEP 621 常见字段)。
"""
from __future__ import annotations
import re
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional

from comfy_mgr.models.scanned_node import ScannedNode


@dataclass
class PkgMeta:
    name: str = ""
    version: Optional[str] = None
    author: Optional[str] = None
    description: Optional[str] = None


_NAME_RE = re.compile(r'^\s*name\s*=\s*"([^"]+)"', re.MULTILINE)
_VERSION_RE = re.compile(r'^\s*version\s*=\s*"([^"]+)"', re.MULTILINE)
_DESCRIPTION_RE = re.compile(r'^\s*description\s*=\s*"([^"]+)"', re.MULTILINE)
_AUTHORS_NAME_RE = re.compile(
    r'\{\s*name\s*=\s*"([^"]+)"[^}]*\}', re.MULTILINE,
)


def _parse_pyproject(pkg_dir: Path) -> PkgMeta:
    """轻量 pyproject.toml 解析。只读 PEP 621 常见字段。"""
    pyproject = pkg_dir / "pyproject.toml"
    if not pyproject.exists():
        return PkgMeta()
    try:
        text = pyproject.read_text(encoding="utf-8", errors="ignore")
    except OSError:
        return PkgMeta()

    name_m = _NAME_RE.search(text)
    ver_m = _VERSION_RE.search(text)
    desc_m = _DESCRIPTION_RE.search(text)
    auth_m = _AUTHORS_NAME_RE.search(text)

    return PkgMeta(
        name=name_m.group(1) if name_m else "",
        version=ver_m.group(1) if ver_m else None,
        author=auth_m.group(1) if auth_m else None,
        description=desc_m.group(1) if desc_m else None,
    )


def _placeholder_node(env_id: str, pkg_dir: Path, error_msg: str) -> ScannedNode:
    """整包扫描失败时,建一个 placeholder node 写入 DB,warnings 记录。"""
    return ScannedNode(
        env_id=env_id,
        package=pkg_dir.name,
        package_path=pkg_dir,
        class_mappings=[],
        status="enabled",
        scan_meta={
            "source": "not_found",
            "warnings": [error_msg],
        },
        last_scanned_at=_now_iso(),
    )


def _now_iso() -> str:
    """UTC ISO8601 字符串,秒精度。M2 内部统一用这个时间格式。"""
    return datetime.now(timezone.utc).isoformat(timespec="seconds")
