"""打 zip 绿色版。

Usage: python scripts/build_zip.py 0.1.0
Output: dist/comfyui-manager-v0.1.0-win64.zip
"""
from __future__ import annotations
import argparse
import os
import shutil
import sys
import zipfile
from pathlib import Path

PROJECT_ROOT = Path(__file__).parent.parent
DEFAULT_EXCLUDE_DIRS = {".git", "__pycache__", ".pytest_cache", ".mypy_cache",
                        ".ruff_cache", "tests", "docs", ".superpowers",
                        "dist", "envs"}
DEFAULT_EXCLUDE_FILES = {".env", "poetry.lock.bak"}


def should_exclude(path: Path) -> bool:
    for part in path.parts:
        if part in DEFAULT_EXCLUDE_DIRS:
            return True
    if path.name in DEFAULT_EXCLUDE_FILES:
        return True
    return False


def build_zip(version: str) -> Path:
    src = PROJECT_ROOT
    out = PROJECT_ROOT / "dist" / f"comfyui-manager-v{version}-win64.zip"
    out.parent.mkdir(parents=True, exist_ok=True)
    if out.exists():
        out.unlink()

    included = []
    for p in src.rglob("*"):
        if p.is_file() and not should_exclude(p.relative_to(src)):
            included.append(p)

    # 占位空目录（catalog/ logs/）
    empty_dirs = [src / "catalog", src / "logs"]
    for d in empty_dirs:
        d.mkdir(parents=True, exist_ok=True)

    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zf:
        for p in included:
            arcname = p.relative_to(src).as_posix()
            zf.write(p, arcname)
        # 空目录用 .gitkeep 占位
        for d in empty_dirs:
            zf.writestr((d.relative_to(src).as_posix() + "/.gitkeep"), "")

    print(f"[OK] Built: {out}")
    print(f"     Files: {len(included)}")
    print(f"     Size:  {out.stat().st_size / 1024 / 1024:.1f} MB")
    return out


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("version", help="版本号，例如 0.1.0")
    args = parser.parse_args()
    build_zip(args.version)


if __name__ == "__main__":
    main()